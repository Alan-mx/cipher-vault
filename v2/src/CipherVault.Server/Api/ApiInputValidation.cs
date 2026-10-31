namespace CipherVault.Server.Api;

public static class ApiInputValidation
{
    public const int MaxPasswordLength = 1024;

    public static string RequirePassword(
        string? value,
        string fieldName)
    {
        if (string.IsNullOrEmpty(value)
            || value.Length > MaxPasswordLength)
        {
            throw new ApiRequestException(
                StatusCodes.Status400BadRequest,
                ApiErrorCodes.InvalidInput,
                $"{fieldName} is required and must not exceed {MaxPasswordLength} characters.");
        }

        return value;
    }
}
