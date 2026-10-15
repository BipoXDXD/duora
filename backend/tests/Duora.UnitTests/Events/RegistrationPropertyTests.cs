using CsCheck;
using Duora.Core.Events;
using static Duora.UnitTests.Events.EventFixtures;

namespace Duora.UnitTests.Events;

/// <summary>
/// Invariantes da lotação sob sequências aleatórias de inscrições, retiradas e check-ins durante a janela de check-in
/// (T-28). Poucas pessoas e capacidade pequena forçam fila, repetição e retirada de quem já saiu. Quando uma
/// propriedade falha, o CsCheck reduz a sequência e imprime a semente que a refaz (variável <c>CsCheck_Seed</c>).
/// </summary>
public sealed class RegistrationPropertyTests
{
    private const int MaxCapacity = 8;
    private const int MaxPeople = 14;
    private const int MaxOperations = 60;
    private const long Iterations = 500;

    private static readonly Gen<Step> AnyStep =
        Gen.Select(Gen.Int[0, 2], Gen.Int[1, MaxPeople], (kind, person) => new Step((StepKind)kind, person));

    private static readonly Gen<Scenario> AnyScenario =
        from capacity in Gen.Int[1, MaxCapacity]
        from steps in AnyStep.Array[0, MaxOperations]
        select new Scenario(capacity, steps);

    private enum StepKind
    {
        Register,
        Withdraw,
        CheckIn,
    }

    [Fact]
    public void SeatsTakenNeverExceedTheCapacity()
    {
        AnyScenario.Sample(scenario =>
        {
            var @event = scenario.Run(_ => { });

            Assert.InRange(@event.ConfirmedCount, 0, scenario.Capacity);
            Assert.Equal(@event.Registrations.Count(r => r.HoldsSeat), @event.ConfirmedCount);
        }, iter: Iterations);
    }

    [Fact]
    public void NobodyHasTwoRegistrations()
    {
        AnyScenario.Sample(scenario =>
        {
            var people = scenario.Run(_ => { }).Registrations.Select(r => r.UserId).ToList();

            Assert.Equal(people.Count, people.Distinct().Count());
        }, iter: Iterations);
    }

    [Fact]
    public void WaitlistKeepsArrivalOrder()
    {
        AnyScenario.Sample(scenario =>
        {
            var @event = scenario.Run(_ => { });
            var waitlistedInArrivalOrder = @event.Registrations
                .Where(r => r.State == RegistrationState.Waitlisted)
                .Select(r => r.UserId);

            Assert.Equal(waitlistedInArrivalOrder, @event.Waitlist.Select(r => r.UserId));
            Assert.All(@event.Waitlist.Zip(@event.Waitlist.Skip(1)), pair =>
                Assert.True(pair.First.WaitlistPosition < pair.Second.WaitlistPosition));
        }, iter: Iterations);
    }

    [Fact]
    public void NobodyWaitsWhileASeatIsFree()
    {
        AnyScenario.Sample(scenario =>
        {
            var @event = scenario.Run(_ => { });

            Assert.True(@event.Waitlist.Count == 0 || @event.ConfirmedCount == scenario.Capacity);
        }, iter: Iterations);
    }

    [Fact]
    public void SeatsAndPromotionsMatchAFirstComeFirstServedQueue()
    {
        AnyScenario.Sample(scenario =>
        {
            var model = new QueueModel(scenario.Capacity);

            var (@event, promotions) = scenario.RunCollectingPromotions(model.Apply);

            Assert.Equal(model.Seated.Order(), @event.Registrations.Where(r => r.HoldsSeat).Select(r => r.UserId).Order());
            Assert.Equal(model.Waiting, @event.Waitlist.Select(r => r.UserId));
            Assert.Equal(model.Promotions, promotions);
        }, iter: Iterations);
    }

    private sealed record Step(StepKind Kind, int Person)
    {
        public Guid UserId => User(Person);

        public override string ToString() => $"{Kind}({Person})";
    }

    private sealed class Scenario(int capacity, Step[] steps)
    {
        public int Capacity { get; } = capacity;

        /// <summary>Aplica os passos num evento com check-in aberto; o observador vê cada passo antes do evento.</summary>
        public Event Run(Action<Step> observe) => RunCollectingPromotions(observe).Event;

        public (Event Event, List<Guid> Promotions) RunCollectingPromotions(Action<Step> observe)
        {
            List<Guid> promotions = [];
            var @event = EventStates.CheckInOpen(Capacity);
            var registrationNumber = 0;
            foreach (var step in steps)
            {
                observe(step);
                var effects = step.Kind switch
                {
                    StepKind.Register => @event.Register(
                        RegistrationId(++registrationNumber), step.UserId, ParticipantEligibility.Eligible, DuringCheckIn).Effects,
                    StepKind.Withdraw => @event.Withdraw(step.UserId, DuringCheckIn).Effects,
                    StepKind.CheckIn => @event.CheckIn(step.UserId, ParticipantEligibility.Eligible, DuringCheckIn).Effects,
                    _ => throw new ArgumentOutOfRangeException(nameof(observe), step.Kind, "Passo desconhecido."),
                };
                promotions.AddRange(effects.OfType<WaitlistedRegistrationPromoted>().Select(promoted => promoted.UserId));
            }

            return (@event, promotions);
        }

        public override string ToString() => $"capacidade {Capacity}: {string.Join(", ", steps.AsEnumerable())}";
    }

    /// <summary>Oráculo independente: fila FIFO com assentos, sem posições nem estados.</summary>
    private sealed class QueueModel(int capacity)
    {
        private readonly HashSet<Guid> _everRegistered = [];

        public List<Guid> Seated { get; } = [];

        public List<Guid> Waiting { get; } = [];

        public List<Guid> Promotions { get; } = [];

        public void Apply(Step step)
        {
            if (step.Kind == StepKind.Register && _everRegistered.Add(step.UserId))
            {
                (Seated.Count < capacity ? Seated : Waiting).Add(step.UserId);
            }
            else if (step.Kind == StepKind.Withdraw)
            {
                Waiting.Remove(step.UserId);
                if (Seated.Remove(step.UserId) && Waiting.Count > 0)
                {
                    Promotions.Add(Waiting[0]);
                    Seated.Add(Waiting[0]);
                    Waiting.RemoveAt(0);
                }
            }
        }
    }
}
