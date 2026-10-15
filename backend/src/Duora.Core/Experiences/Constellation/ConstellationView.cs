namespace Duora.Core.Experiences.Constellation;

/// <summary>
/// Projeção de Constelação de Escolhas para um participante (glossário: <c>ParticipantView</c>). Montada do zero: a
/// resposta da outra pessoa só existe neste tipo depois da revelação, e ids de participante e registro de ações não
/// existem nele (INV-05).
/// </summary>
public sealed record ConstellationView(
    string ExperienceId,
    int RulesVersion,
    int ContentVersion,
    int SchemaVersion,
    long ResourceVersion,
    SessionStatus Status,
    DateTimeOffset Deadline,
    ConstellationStage Stage,
    string Title,
    string Objective,
    int CardCount,
    CardView? CurrentCard,
    OwnAnswerView? MyAnswer,
    bool AwaitingPartner,
    IReadOnlyList<RevealedCardView> Revealed,
    IReadOnlyList<PassedCardView> Passed,
    IReadOnlyList<string> AvailableActions);

/// <summary>Cartão aberto. <c>Position</c> conta a partir de 1, para "Cartão 2 de 3".</summary>
public sealed record CardView(string CardId, int Position, string Prompt, IReadOnlyList<CardOptionView> Options);

public sealed record CardOptionView(string OptionId, string Label);

/// <summary>A própria resposta ao cartão atual. <see cref="Option"/> só existe quando a pessoa escolheu.</summary>
public sealed record OwnAnswerView(OwnAnswerKind Kind, CardOptionView? Option);

public enum OwnAnswerKind
{
    Chosen = 1,
    Skipped = 2,
}

/// <summary>Cartão revelado por dupla confirmação. Sem pontuação: só as duas escolhas e se coincidiram.</summary>
public sealed record RevealedCardView(
    string CardId, string Prompt, string MyOptionLabel, string PartnerOptionLabel, bool ChoseTheSame);

/// <summary>Cartão encerrado sem revelação. Não diz quem pulou nem o que alguém escolheu.</summary>
public sealed record PassedCardView(string CardId, string Prompt);
