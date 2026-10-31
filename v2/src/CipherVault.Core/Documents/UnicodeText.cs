namespace CipherVault.Core.Documents;

internal static class UnicodeText
{
    internal static int CountScalars(string value)
    {
        var count = 0;
        for (var index = 0; index < value.Length; index++)
        {
            if (char.IsHighSurrogate(value[index]))
            {
                if (index + 1 >= value.Length
                    || !char.IsLowSurrogate(value[index + 1]))
                {
                    throw new ArgumentException(
                        "Text contains an invalid Unicode surrogate pair.",
                        nameof(value));
                }

                index++;
            }
            else if (char.IsLowSurrogate(value[index]))
            {
                throw new ArgumentException(
                    "Text contains an invalid Unicode surrogate pair.",
                    nameof(value));
            }

            count++;
        }

        return count;
    }

    internal static string CollapseWhitespace(string value)
    {
        return string.Join(
            ' ',
            value
                .Normalize(System.Text.NormalizationForm.FormC)
                .Trim()
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }
}
