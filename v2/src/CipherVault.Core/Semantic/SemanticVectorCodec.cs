using System.Buffers.Binary;

namespace CipherVault.Core.Semantic;

public static class SemanticVectorCodec
{
    private const double MinimumNorm = 1e-12;

    public static byte[] NormalizeAndEncode(float[] vector)
    {
        var normalized = Normalize(vector);
        var bytes = new byte[checked(normalized.Length * sizeof(float))];
        for (var index = 0; index < normalized.Length; index++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(
                bytes.AsSpan(index * sizeof(float), sizeof(float)),
                normalized[index]);
        }
        return bytes;
    }

    public static float[] Normalize(float[] vector)
    {
        ArgumentNullException.ThrowIfNull(vector);
        if (vector.Length == 0)
        {
            throw InvalidVector("Embedding vector must not be empty.");
        }
        var sum = 0d;
        foreach (var value in vector)
        {
            if (!float.IsFinite(value))
            {
                throw InvalidVector(
                    "Embedding vector contains a non-finite value.");
            }
            sum += value * value;
        }
        var norm = Math.Sqrt(sum);
        if (norm <= MinimumNorm)
        {
            throw InvalidVector("Embedding vector must not be zero.");
        }
        var normalized = new float[vector.Length];
        for (var index = 0; index < vector.Length; index++)
        {
            normalized[index] = (float)(vector[index] / norm);
        }
        return normalized;
    }

    public static float[] Decode(byte[] vectorBlob, int dimension)
    {
        ArgumentNullException.ThrowIfNull(vectorBlob);
        if (dimension < 1
            || vectorBlob.Length != checked(dimension * sizeof(float)))
        {
            throw InvalidVector("Embedding vector dimension is invalid.");
        }
        var vector = new float[dimension];
        for (var index = 0; index < dimension; index++)
        {
            vector[index] = BinaryPrimitives.ReadSingleLittleEndian(
                vectorBlob.AsSpan(index * sizeof(float), sizeof(float)));
            if (!float.IsFinite(vector[index]))
            {
                throw InvalidVector(
                    "Embedding vector contains a non-finite value.");
            }
        }
        return vector;
    }

    public static double Dot(float[] left, float[] right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        if (left.Length == 0 || left.Length != right.Length)
        {
            throw InvalidVector(
                "Embedding vectors must have the same non-zero dimension.");
        }
        var sum = 0d;
        for (var index = 0; index < left.Length; index++)
        {
            sum += left[index] * right[index];
        }
        return sum;
    }

    private static ArgumentException InvalidVector(string message) =>
        new(message);
}
