using CsCheck;
using Duora.Core.Events;
using static Duora.UnitTests.Events.EventFixtures;

namespace Duora.UnitTests.Events;

/// <summary>
/// Invariantes da lotação sob sequências aleatórias de inscrições, retiradas e check-ins (T-28). O evento começa em
/// <c>Published</c> ou <c>CheckInOpen</c>, cada passo acontece num de quatro instantes em ordem (antes do check-in, na
/// abertura, 1 tick antes do início e no início) e parte das pessoas está inelegível. Poucas pessoas e capacidade
/// pequena forçam fila, repetição e retirada de quem já saiu. Quando uma propriedade falha, o CsCheck reduz a
/// sequência e imprime a semente que a refaz (variável <c>CsCheck_Seed</c>).
/// </summary>
public sealed class RegistrationPropertyTests
{
    private const int MaxCapacity = 8;
    private const int MaxPeople = 14;
    private const int MaxOperations = 60;
    private const int IneligibleOneIn = 5;
    private const long Iterations = 500;

    private static readonly Gen<Scenario> AnyScenario =
        from capacity in Gen.Int[1, MaxCapacity]
        from checkInAlreadyOpen in Gen.Bool
        from steps in AnyStep(checkInAlreadyOpen ? Moment.CheckInOpening : Moment.BeforeCheckIn).Array[0, MaxOperations]
        select new Scenario(capacity, checkInAlreadyOpen, steps);

    private enum StepKind
    {
        Register,
        Withdraw,
        CheckIn,
    }

    /// <summary>Instantes em ordem cronológica; o valor do enum é a ordem.</summary>
    private enum Moment
    {
        BeforeCheckIn,
        CheckInOpening,
        JustBeforeStart,
        AtStart,
    }

    [Fact]
    public void SeatsTakenNeverExceedTheCapacityAndMatchTheRegistrations()
    {
        AnyScenario.Sample(scenario => scenario.Run((_, _, @event) =>
        {
            Assert.InRange(@event.ConfirmedCount, 0, scenario.Capacity);
            Assert.Equal(@event.Registrations.Count(r => r.HoldsSeat), @event.ConfirmedCount);
        }), iter: Iterations);
    }

    [Fact]
    public void NobodyHasTwoRegistrations()
    {
        AnyScenario.Sample(scenario => scenario.Run((_, _, @event) =>
        {
            var people = @event.Registrations.Select(r => r.UserId).ToList();
            Assert.Equal(people.Count, people.Distinct().Count());
        }), iter: Iterations);
    }

    [Fact]
    public void WaitlistKeepsArrivalOrderWithPositionsBelowTheCounter()
    {
        AnyScenario.Sample(scenario => scenario.Run((_, _, @event) =>
        {
            var waitlistedInArrivalOrder = @event.Registrations
                .Where(r => r.State == RegistrationState.Waitlisted)
                .Select(r => r.UserId);
            Assert.Equal(waitlistedInArrivalOrder, @event.Waitlist.Select(r => r.UserId));
            Assert.All(@event.Waitlist.Zip(@event.Waitlist.Skip(1)), pair =>
                Assert.True(pair.First.WaitlistPosition < pair.Second.WaitlistPosition));
            Assert.All(@event.Waitlist, r => Assert.InRange(r.WaitlistPosition ?? 0, 1, @event.NextWaitlistPosition - 1));
        }), iter: Iterations);
    }

    /// <summary>
    /// Mecanismo FIFO, não política: o oráculo recebe só os passos que o evento aceitou e as promoções que ele
    /// decidiu fazer, e confere onde cada pessoa entrou, quem saiu e quem foi promovido. Quando inscrever, recusar ou
    /// promover (horário, estado, elegibilidade) fica com as propriedades de política abaixo.
    /// </summary>
    [Fact]
    public void AcceptedStepsFollowAFirstComeFirstServedQueue()
    {
        AnyScenario.Sample(scenario =>
        {
            var model = new FifoQueueModel(scenario.Capacity);
            scenario.Run((step, outcome, @event) =>
            {
                model.Apply(step, outcome);
                Assert.Equal(model.Seated.Order(), @event.Registrations.Where(r => r.HoldsSeat).Select(r => r.UserId).Order());
                Assert.Equal(model.Waiting, @event.Waitlist.Select(r => r.UserId));
            });
        }, iter: Iterations);
    }

    /// <summary>Política: antes do horário de início, ninguém espera com vaga livre.</summary>
    [Fact]
    public void BeforeTheStartNobodyWaitsWhileASeatIsFree()
    {
        AnyScenario.Sample(scenario => scenario.Run((step, _, @event) =>
        {
            if (step.Moment < Moment.AtStart)
            {
                Assert.True(@event.Waitlist.Count == 0 || @event.ConfirmedCount == scenario.Capacity);
            }
        }), iter: Iterations);
    }

    /// <summary>Política: do horário de início em diante, nada de inscrição nova nem de promoção.</summary>
    [Fact]
    public void FromTheStartNobodyRegistersOrIsPromoted()
    {
        AnyScenario.Sample(scenario => scenario.Run((step, outcome, _) =>
        {
            if (step.Moment == Moment.AtStart)
            {
                Assert.False(step.Kind == StepKind.Register && outcome.IsAccepted);
                Assert.Empty(outcome.Effects.OfType<WaitlistedRegistrationPromoted>());
            }
        }), iter: Iterations);
    }

    /// <summary>
    /// Política (INV-01): inscrição ou check-in de pessoa inelegível não muda a situação dela. Repetir um check-in já
    /// feito é aceito sem efeito, então a propriedade compara o estado antes e depois, não o aceite.
    /// </summary>
    [Fact]
    public void IneligibleRegistrationOrCheckInChangesNothing()
    {
        AnyScenario.Sample(scenario => scenario.Run((step, outcome, @event) =>
        {
            if (!step.Eligible && step.Kind != StepKind.Withdraw)
            {
                Assert.Equal(outcome.StateBefore, @event.RegistrationOf(step.UserId)?.State);
                Assert.Empty(outcome.Effects);
            }
        }), iter: Iterations);
    }

    private static Gen<Step> AnyStep(Moment earliest) =>
        Gen.Select(
            Gen.Int[0, 2],
            Gen.Int[1, MaxPeople],
            Gen.Int[(int)earliest, (int)Moment.AtStart],
            Gen.Int[1, IneligibleOneIn],
            (kind, person, moment, eligibilityDraw) =>
                new Step((StepKind)kind, person, (Moment)moment, Eligible: eligibilityDraw != 1));

    private static DateTimeOffset InstantOf(Moment moment) => moment switch
    {
        Moment.BeforeCheckIn => BeforeCheckIn.AddHours(1),
        Moment.CheckInOpening => CheckInOpensAt,
        Moment.JustBeforeStart => StartsAt - TimeSpan.FromTicks(1),
        Moment.AtStart => StartsAt,
        _ => throw new ArgumentOutOfRangeException(nameof(moment), moment, "Instante desconhecido."),
    };

    private sealed record Step(StepKind Kind, int Person, Moment Moment, bool Eligible)
    {
        public Guid UserId => User(Person);

        public ParticipantEligibility Eligibility =>
            Eligible ? ParticipantEligibility.Eligible : ParticipantEligibility.Ineligible;

        public override string ToString() => $"{Kind}({Person}{(Eligible ? string.Empty : ", inelegível")}) em {Moment}";
    }

    /// <summary>Resultado de um passo e o estado da inscrição da pessoa logo antes dele.</summary>
    private sealed record StepOutcome(bool IsAccepted, IReadOnlyList<Effect> Effects, RegistrationState? StateBefore);

    private sealed class Scenario(int capacity, bool checkInAlreadyOpen, Step[] steps)
    {
        public int Capacity { get; } = capacity;

        /// <summary>Aplica os passos em ordem cronológica e entrega cada resultado e o evento logo depois dele.</summary>
        public void Run(Action<Step, StepOutcome, Event> observe)
        {
            var @event = checkInAlreadyOpen ? EventStates.CheckInOpen(Capacity) : EventStates.Published(Capacity);
            var registrationNumber = 0;
            foreach (var step in steps.OrderBy(step => step.Moment))
            {
                var now = InstantOf(step.Moment);
                var stateBefore = @event.RegistrationOf(step.UserId)?.State;
                var outcome = step.Kind switch
                {
                    StepKind.Register => Observe(
                        @event.Register(RegistrationId(++registrationNumber), step.UserId, step.Eligibility, now),
                        stateBefore),
                    StepKind.Withdraw => Observe(@event.Withdraw(step.UserId, now), stateBefore),
                    StepKind.CheckIn => Observe(@event.CheckIn(step.UserId, step.Eligibility, now), stateBefore),
                    _ => throw new ArgumentOutOfRangeException(nameof(observe), step.Kind, "Passo desconhecido."),
                };
                observe(step, outcome, @event);
            }
        }

        public override string ToString() =>
            $"capacidade {Capacity}, {(checkInAlreadyOpen ? "CheckInOpen" : "Published")}: "
            + string.Join(", ", steps.OrderBy(step => step.Moment));

        private static StepOutcome Observe<TRefusal>(Outcome<TRefusal> outcome, RegistrationState? stateBefore)
            where TRefusal : struct, Enum => new(outcome.IsAccepted, outcome.Effects, stateBefore);
    }

    /// <summary>
    /// Oráculo independente do mecanismo: assentos e uma fila FIFO, sem posições, estados nem regras de horário ou
    /// elegibilidade. Uma inscrição aceita senta se houver assento, senão vai para o fim da fila; uma promoção
    /// decidida pelo evento tem de ser a cabeça da fila.
    /// </summary>
    private sealed class FifoQueueModel(int capacity)
    {
        public List<Guid> Seated { get; } = [];

        public List<Guid> Waiting { get; } = [];

        public void Apply(Step step, StepOutcome outcome)
        {
            if (!outcome.IsAccepted)
            {
                return;
            }

            if (step.Kind == StepKind.Register)
            {
                (Seated.Count < capacity ? Seated : Waiting).Add(step.UserId);
            }
            else if (step.Kind == StepKind.Withdraw)
            {
                Waiting.Remove(step.UserId);
                Seated.Remove(step.UserId);
                foreach (var promoted in outcome.Effects.OfType<WaitlistedRegistrationPromoted>())
                {
                    Assert.Equal(Waiting[0], promoted.UserId);
                    Waiting.RemoveAt(0);
                    Seated.Add(promoted.UserId);
                }
            }
        }
    }
}
