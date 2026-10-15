using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using Duora.Core.Experiences;
using Duora.Core.Experiences.UnlikelyRoutes;
using static Duora.UnitTests.Experiences.UnlikelyRoutesFixture;
using Validation = Duora.Core.Experiences.ActionValidation<
    Duora.Core.Experiences.UnlikelyRoutes.UnlikelyRoutesState,
    Duora.Core.Experiences.UnlikelyRoutes.UnlikelyRoutesAction>;

namespace Duora.UnitTests.Experiences;

/// <summary>
/// Registro de ações aplicadas (spec 8.7, INV-05 e INV-06): guarda o mínimo para reconhecer reenvio e reuso do id,
/// nunca o corpo da ação. A opção escolhida já vive no estado da experiência; uma cópia no registro duraria além
/// dele e iria para <c>game_actions</c> (ADR-0020).
/// </summary>
public sealed class ActionLogTests
{
    // O registro é interno ao Core de propósito e ainda não tem forma persistida (ADR-0020 proposto). A reflexão
    // lê os dados em repouso exatamente como uma serialização futura os leria.
    private const string ActionLogProperty = "ActionLog";

    private static readonly JsonSerializerOptions PlainText = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly UnlikelyRoutesEngine _engine = Engine(ClockAtRoundStart());

    [Fact]
    public void ResendingAnEqualCommandBuiltAgainReturnsTheOriginalReceipt()
    {
        var session = AtChoices(_engine, NewSession(_engine));
        var actionId = new ActionId(Guid.Parse("0199d700-0000-7000-8000-0000000000a1"));
        var first = new GameCommand<UnlikelyRoutesAction>(
            actionId, Ana, session.Version, new UnlikelyRoutesAction.ChooseOption("route-stop-1", "lighthouse"));
        session = Apply(_engine, session, first);
        var resent = new GameCommand<UnlikelyRoutesAction>(
            actionId, Ana, new StateVersion(5), new UnlikelyRoutesAction.ChooseOption("route-stop-1", "lighthouse"));

        var validation = _engine.ValidateAction(session, resent);

        var replayed = Assert.IsType<Validation.AlreadyApplied>(validation);
        Assert.Equal(new ActionReceipt(actionId, new StateVersion(6)), replayed.Receipt);
    }

    [Fact]
    public void ReusingAnActionIdWithAnotherOptionForTheSameStopIsRejected()
    {
        var session = AtChoices(_engine, NewSession(_engine));
        var first = Command(session, Ana, new UnlikelyRoutesAction.ChooseOption("route-stop-1", "lighthouse"));
        session = Apply(_engine, session, first);

        var reused = first with { Action = new UnlikelyRoutesAction.ChooseOption("route-stop-1", "floating-market") };

        var rejected = Assert.IsType<Validation.Rejected>(_engine.ValidateAction(session, reused));
        Assert.Equal(RejectionReason.ActionIdReused, rejected.Reason);
    }

    [Fact]
    public void ActionLogDoesNotKeepTheChosenOption()
    {
        var session = AtChoices(_engine, NewSession(_engine));
        var choose = Command(session, Ana, new UnlikelyRoutesAction.ChooseOption("route-stop-1", "floating-market"));
        session = Apply(_engine, session, choose);

        var json = SerializeActionLog(session);

        Assert.DoesNotContain("floating-market", json, StringComparison.Ordinal);
        Assert.DoesNotContain("route-stop-1", json, StringComparison.Ordinal);
    }

    [Fact]
    public void ActionLogKeepsTheMinimalReceiptOfEachAction()
    {
        var session = AtChoices(_engine, NewSession(_engine));
        var choose = Command(session, Ana, new UnlikelyRoutesAction.ChooseOption("route-stop-1", "floating-market"));
        session = Apply(_engine, session, choose);

        using var document = JsonDocument.Parse(SerializeActionLog(session));
        var recorded = document.RootElement.EnumerateArray()
            .Single(entry => entry.GetProperty("ActionId").GetProperty("Value").GetString() == choose.ActionId.Value.ToString());

        // Sentinela do teste acima: a serialização do registro funciona e carrega o recibo, só não o corpo.
        Assert.Equal(
            ["ActionId", "Actor", "ExpectedVersion", "Fingerprint", "ResultingVersion", "Type"],
            recorded.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        Assert.Equal("choose-option", recorded.GetProperty("Type").GetProperty("Value").GetString());
        Assert.Equal(
            ActionFingerprint.Of(new UnlikelyRoutesAction.ChooseOption("route-stop-1", "floating-market")).Sha256Hex,
            recorded.GetProperty("Fingerprint").GetProperty("Sha256Hex").GetString());
    }

    private static string SerializeActionLog(UnlikelyRoutesSession session)
    {
        var property = typeof(UnlikelyRoutesSession).GetProperty(
            ActionLogProperty, BindingFlags.Instance | BindingFlags.NonPublic)!;
        var log = (IDictionary)property.GetValue(session)!;
        return JsonSerializer.Serialize(ToTree(log.Values), PlainText);
    }

    /// <summary>
    /// Árvore pelo tipo em tempo de execução. O System.Text.Json serializa pelo tipo declarado, então uma ação
    /// guardada como a base abstrata sairia só com <c>Type</c> e o teste de privacidade passaria à toa.
    /// </summary>
    private static object? ToTree(object? value) => value switch
    {
        null => null,
        string or Guid or Enum => Convert.ToString(value, CultureInfo.InvariantCulture),
        _ when value.GetType().IsPrimitive => Convert.ToString(value, CultureInfo.InvariantCulture),
        IEnumerable items => items.Cast<object?>().Select(ToTree).ToList(),
        _ => value.GetType()
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(property => property.GetIndexParameters().Length == 0)
            .ToDictionary(property => property.Name, property => ToTree(property.GetValue(value))),
    };
}
