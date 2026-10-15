namespace Duora.Core.Experiences;

/// <summary>Resultado mínimo de uma ação aplicada (spec 8.6): o id da ação e a versão do estado que ela gerou.</summary>
public sealed record ActionReceipt(ActionId ActionId, StateVersion ResourceVersion);
