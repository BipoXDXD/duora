using Duora.Contracts.Profiles;
using Duora.Core.Profiles;
using Duora.Web.Accounts;

namespace Duora.Web.Profiles;

/// <summary>
/// Fronteira do PATCH do perfil: converte o DTO em <see cref="ProfileEdit"/> ou acumula erros com códigos estáveis
/// por campo. Os erros nunca repetem o texto recebido, que pode conter dado pessoal.
/// </summary>
internal static class ProfileEditParser
{
    public const string NullNotAllowed = "NULL_NOT_ALLOWED";

    private const string DisplayNameField = "displayName";
    private const string BioField = "bio";

    public static ProfileEdit? Parse(UpdateProfileRequest request, out Dictionary<string, string[]> errors)
    {
        errors = [];
        var displayName = ParseDisplayName(request.DisplayName, errors);
        var bio = ParseBio(request.Bio, errors);
        return errors.Count == 0 ? new ProfileEdit(displayName, bio) : null;
    }

    private static DisplayName? ParseDisplayName(PatchField<string> field, Dictionary<string, string[]> errors)
    {
        if (!field.IsPresent)
        {
            return null;
        }

        if (field.Value is null)
        {
            errors[DisplayNameField] = [NullNotAllowed];
            return null;
        }

        if (DisplayName.TryParse(field.Value, out var name, out var problem))
        {
            return name;
        }

        errors[DisplayNameField] = [CodeFor(problem)];
        return null;
    }

    private static BioChange? ParseBio(PatchField<string> field, Dictionary<string, string[]> errors)
    {
        if (!field.IsPresent)
        {
            return null;
        }

        if (field.Value is null)
        {
            return new BioChange(null);
        }

        if (ProfileBio.TryParse(field.Value, out var bio, out var problem))
        {
            return new BioChange(bio);
        }

        errors[BioField] = [CodeFor(problem)];
        return null;
    }

    private static string CodeFor(ProfileTextProblem problem) => problem switch
    {
        ProfileTextProblem.Empty => RequestValidation.Required,
        ProfileTextProblem.TooLong => RequestValidation.TooLong,
        ProfileTextProblem.ForbiddenCharacter => RequestValidation.InvalidCharacters,
        _ => throw new ArgumentOutOfRangeException(nameof(problem), problem, "Problema de texto sem código."),
    };
}
