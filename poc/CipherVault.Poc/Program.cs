using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Konscious.Security.Cryptography;
using Microsoft.Data.Sqlite;
using SQLitePCL;

Batteries_V2.Init();

var failures = new List<string>();
var passes = new List<string>();

void Check(bool condition, string name)
{
    if (condition)
    {
        passes.Add(name);
        Console.WriteLine($"[PASS] {name}");
    }
    else
    {
        failures.Add(name);
        Console.WriteLine($"[FAIL] {name}");
    }
}

var runId = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss");
var artifactRoot = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "poc", "artifacts", runId));
var vaultDir = Path.Combine(artifactRoot, "vault");
var backupDir = Path.Combine(artifactRoot, "backup");
Directory.CreateDirectory(vaultDir);
Directory.CreateDirectory(backupDir);

var dbPath = Path.Combine(vaultDir, "vault.db");
var keyPath = Path.Combine(vaultDir, "vault.keys");
var tamperPath = Path.Combine(artifactRoot, "tampered.db");
var reportPath = Path.Combine(artifactRoot, "poc-report.txt");

var marker = "PV_SECRET_MARKER_9F7C1F38";
var chineseMarker = "睡眠质量明显下降，半夜经常醒来。";
var vaultId = Guid.NewGuid();
var masterKey = RandomNumberGenerator.GetBytes(32);
var dbKey = HKDF.DeriveKey(
    HashAlgorithmName.SHA256,
    masterKey,
    32,
    salt: null,
    info: Encoding.UTF8.GetBytes("pv/db/v1"));

var password = "correct horse battery staple 2026";
var passwordSalt = RandomNumberGenerator.GetBytes(16);
var passwordKek = DeriveArgon2(password, passwordSalt);
var passwordSlot = WrapMasterKey(masterKey, passwordKek, vaultId, "password");

var recoveryKey = RandomNumberGenerator.GetBytes(32);
var recoverySalt = RandomNumberGenerator.GetBytes(16);
var recoveryKek = HKDF.DeriveKey(
    HashAlgorithmName.SHA256,
    recoveryKey,
    32,
    recoverySalt,
    Encoding.UTF8.GetBytes("pv/recovery/v1"));
var recoverySlot = WrapMasterKey(masterKey, recoveryKek, vaultId, "recovery");

var envelope = new KeyEnvelope
{
    Format = "cipher-vault-keyring",
    FormatVersion = 1,
    VaultId = vaultId,
    KeyGeneration = 1,
    CreatedAt = DateTimeOffset.UtcNow,
    PasswordSlot = passwordSlot,
    RecoverySlot = recoverySlot
};

WriteKeyEnvelopeAtomic(keyPath, envelope);
Check(File.Exists(keyPath), "POC-04 key envelope file created");

var envelopeFromDisk = JsonSerializer.Deserialize<KeyEnvelope>(File.ReadAllText(keyPath))
    ?? throw new InvalidOperationException("key envelope deserialize failed");

var unwrappedByPassword = UnwrapMasterKey(
    envelopeFromDisk.PasswordSlot,
    passwordKek,
    vaultId,
    "password");
Check(unwrappedByPassword.SequenceEqual(masterKey), "POC-04 password slot unwraps master key");

var unwrappedByRecovery = UnwrapMasterKey(
    envelopeFromDisk.RecoverySlot,
    recoveryKek,
    vaultId,
    "recovery");
Check(unwrappedByRecovery.SequenceEqual(masterKey), "POC-04 recovery slot unwraps master key");

CreateSqlCipherDatabase(dbPath, dbKey, marker, chineseMarker);

var cipherVersion = ReadScalar(dbPath, dbKey, "PRAGMA cipher_version;");
Check(!string.IsNullOrWhiteSpace(cipherVersion), $"POC-02 SQLCipher loaded ({cipherVersion})");

var first16 = File.ReadAllBytes(dbPath).Take(16).ToArray();
Check(
    !first16.SequenceEqual(Encoding.ASCII.GetBytes("SQLite format 3\0")),
    "POC-02 database does not use plaintext SQLite header");

var wrongKeyRejected = false;
try
{
    var wrongKey = RandomNumberGenerator.GetBytes(32);
    _ = ReadScalar(dbPath, wrongKey, "SELECT count(*) FROM sqlite_master;");
}
catch (SqliteException)
{
    wrongKeyRejected = true;
}
Check(wrongKeyRejected, "POC-02 wrong key cannot read database");

var chineseFtsCount = ReadScalar(
    dbPath,
    dbKey,
    "SELECT count(*) FROM documents_fts WHERE documents_fts MATCH '睡眠质量';");
Check(chineseFtsCount == "1", "POC-02 FTS5 trigram finds Chinese phrase");

var englishFtsCount = ReadScalar(
    dbPath,
    dbKey,
    "SELECT count(*) FROM documents_fts WHERE documents_fts MATCH 'sleep';");
Check(englishFtsCount == "1", "POC-02 FTS5 trigram finds English term");

var tamperRejected = false;
try
{
    var dbBytes = File.ReadAllBytes(dbPath);
    var tamperOffset = Math.Min(4096 + 128, dbBytes.Length - 1);
    dbBytes[tamperOffset] ^= 0x5A;
    File.WriteAllBytes(tamperPath, dbBytes);
    _ = ReadScalar(tamperPath, dbKey, "PRAGMA cipher_integrity_check;");
    _ = ReadScalar(tamperPath, dbKey, "SELECT sum(length(content)) FROM documents;");
}
catch (SqliteException)
{
    tamperRejected = true;
}
Check(tamperRejected, "POC-02 tampered ciphertext is rejected");

var allVaultFiles = Directory.GetFiles(vaultDir, "*", SearchOption.TopDirectoryOnly);
var plaintextFound = allVaultFiles.Any(path => ContainsUtf8OrUtf16(path, marker));
var chinesePlaintextFound = allVaultFiles.Any(path => ContainsUtf8OrUtf16(path, chineseMarker));
Check(!plaintextFound, "POC-03 vault files do not contain ASCII plaintext marker");
Check(!chinesePlaintextFound, "POC-03 vault files do not contain Chinese plaintext marker");

var journalFiles = allVaultFiles
    .Where(path =>
        path.EndsWith("-journal", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith("-wal", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith("-shm", StringComparison.OrdinalIgnoreCase))
    .ToArray();
Check(journalFiles.Length == 0, "POC-03 no persistent journal/WAL/SHM after clean close");

var keyFileBytes = File.ReadAllBytes(keyPath);
Check(!ContainsBytes(keyFileBytes, masterKey), "POC-04 key file does not contain raw master key");
Check(!ContainsBytes(keyFileBytes, Encoding.UTF8.GetBytes(password)), "POC-04 key file does not contain password");

var rotatedPassword = "new correct horse battery staple 2026";
var rotatedSalt = RandomNumberGenerator.GetBytes(16);
var rotatedKek = DeriveArgon2(rotatedPassword, rotatedSalt);
var rotatedEnvelope = envelopeFromDisk with
{
    KeyGeneration = envelopeFromDisk.KeyGeneration + 1,
    PasswordSlot = WrapMasterKey(masterKey, rotatedKek, vaultId, "password")
};
WriteKeyEnvelopeAtomic(keyPath, rotatedEnvelope);

var rotatedFromDisk = JsonSerializer.Deserialize<KeyEnvelope>(File.ReadAllText(keyPath))
    ?? throw new InvalidOperationException("rotated key envelope deserialize failed");
var rotatedUnwrapped = UnwrapMasterKey(
    rotatedFromDisk.PasswordSlot,
    rotatedKek,
    vaultId,
    "password");
Check(rotatedUnwrapped.SequenceEqual(masterKey), "POC-04 password rotation keeps master key recoverable");

File.Copy(dbPath, Path.Combine(backupDir, "vault.db"));
File.Copy(keyPath, Path.Combine(backupDir, "vault.keys"));
var backupEnvelope = JsonSerializer.Deserialize<KeyEnvelope>(
    File.ReadAllText(Path.Combine(backupDir, "vault.keys")))
    ?? throw new InvalidOperationException("backup key envelope deserialize failed");
var backupRecoveryMaster = UnwrapMasterKey(
    backupEnvelope.RecoverySlot,
    recoveryKek,
    vaultId,
    "recovery");
var backupDbKey = HKDF.DeriveKey(
    HashAlgorithmName.SHA256,
    backupRecoveryMaster,
    32,
    salt: null,
    info: Encoding.UTF8.GetBytes("pv/db/v1"));
var backupMarker = ReadScalar(
    Path.Combine(backupDir, "vault.db"),
    backupDbKey,
    "SELECT content FROM documents LIMIT 1;");
Check(backupMarker?.Contains(marker, StringComparison.Ordinal) == true, "POC-04 encrypted backup opens in clean directory");

var reportLines = new List<string>
{
    $"Run: {runId}",
    $"Artifacts: {artifactRoot}",
    $"SQLCipher: {cipherVersion}",
    $"Passed: {passes.Count}",
    $"Failed: {failures.Count}",
    "",
    "PASS:"
};
reportLines.AddRange(passes.Select(item => $"- {item}"));
reportLines.Add("");
reportLines.Add("FAIL:");
reportLines.AddRange(failures.Select(item => $"- {item}"));
var report = string.Join(Environment.NewLine, reportLines);
File.WriteAllText(reportPath, report);
Console.WriteLine();
Console.WriteLine(report);

return failures.Count == 0 ? 0 : 1;

static byte[] DeriveArgon2(string password, byte[] salt)
{
    using var argon2 = new Argon2id(Encoding.UTF8.GetBytes(password))
    {
        Salt = salt,
        DegreeOfParallelism = 1,
        Iterations = 3,
        MemorySize = 64 * 1024
    };
    return argon2.GetBytes(32);
}

static KeySlot WrapMasterKey(byte[] masterKey, byte[] kek, Guid vaultId, string kind)
{
    var nonce = RandomNumberGenerator.GetBytes(12);
    var ciphertext = new byte[masterKey.Length];
    var tag = new byte[16];
    var aad = Encoding.UTF8.GetBytes($"cipher-vault-keyring-v1|{vaultId}|{kind}");
    using var aes = new AesGcm(kek, 16);
    aes.Encrypt(nonce, masterKey, ciphertext, tag, aad);

    var wrapped = new byte[ciphertext.Length + tag.Length];
    Buffer.BlockCopy(ciphertext, 0, wrapped, 0, ciphertext.Length);
    Buffer.BlockCopy(tag, 0, wrapped, ciphertext.Length, tag.Length);
    return new KeySlot
    {
        Kdf = kind == "password" ? "argon2id" : "hkdf-sha256",
        SaltB64 = Convert.ToBase64String(Array.Empty<byte>()),
        NonceB64 = Convert.ToBase64String(nonce),
        WrappedMasterKeyB64 = Convert.ToBase64String(wrapped)
    };
}

static byte[] UnwrapMasterKey(KeySlot slot, byte[] kek, Guid vaultId, string kind)
{
    var nonce = Convert.FromBase64String(slot.NonceB64);
    var wrapped = Convert.FromBase64String(slot.WrappedMasterKeyB64);
    var ciphertext = wrapped[..^16];
    var tag = wrapped[^16..];
    var masterKey = new byte[ciphertext.Length];
    var aad = Encoding.UTF8.GetBytes($"cipher-vault-keyring-v1|{vaultId}|{kind}");
    using var aes = new AesGcm(kek, 16);
    aes.Decrypt(nonce, ciphertext, tag, masterKey, aad);
    return masterKey;
}

static void WriteKeyEnvelopeAtomic(string path, KeyEnvelope envelope)
{
    var tempPath = path + ".tmp";
    var json = JsonSerializer.Serialize(envelope, new JsonSerializerOptions { WriteIndented = true });
    using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
    using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
    {
        writer.Write(json);
        writer.Flush();
        stream.Flush(flushToDisk: true);
    }

    File.Move(tempPath, path, overwrite: true);
}

static void CreateSqlCipherDatabase(string path, byte[] key, string marker, string chineseMarker)
{
    using var connection = OpenSqlCipher(path, key);
    Execute(connection, "PRAGMA journal_mode=DELETE;");
    Execute(connection, "PRAGMA temp_store=MEMORY;");
    Execute(connection, "PRAGMA secure_delete=ON;");
    Execute(connection, "PRAGMA synchronous=FULL;");
    Execute(connection, "PRAGMA cipher_memory_security=ON;");
    Execute(connection, """
        CREATE TABLE documents (
            id INTEGER PRIMARY KEY,
            title TEXT NOT NULL,
            content TEXT NOT NULL
        );
        """);
    Execute(connection, """
        CREATE VIRTUAL TABLE documents_fts USING fts5(
            title,
            content,
            tokenize='trigram'
        );
        """);
    Execute(connection, "BEGIN;");
    using (var command = connection.CreateCommand())
    {
        command.CommandText = "INSERT INTO documents(title, content) VALUES ($title, $content);";
        command.Parameters.AddWithValue("$title", "睡眠记录");
        command.Parameters.AddWithValue(
            "$content",
            $"{chineseMarker} This is a sleep log. {marker} " + new string('x', 9000));
        command.ExecuteNonQuery();
    }
    Execute(connection, "INSERT INTO documents_fts(title, content) SELECT title, content FROM documents;");
    Execute(connection, "COMMIT;");
}

static SqliteConnection OpenSqlCipher(string path, byte[] key)
{
    var connection = new SqliteConnection(
        new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        }.ToString());
    connection.Open();
    using var command = connection.CreateCommand();
    command.CommandText = $"PRAGMA key = \"x'{Convert.ToHexString(key)}'\";";
    command.ExecuteNonQuery();
    return connection;
}

static void Execute(SqliteConnection connection, string sql)
{
    using var command = connection.CreateCommand();
    command.CommandText = sql;
    command.ExecuteNonQuery();
}

static string? ReadScalar(string path, byte[] key, string sql)
{
    using var connection = OpenSqlCipher(path, key);
    using var command = connection.CreateCommand();
    command.CommandText = sql;
    return Convert.ToString(command.ExecuteScalar());
}

static bool ContainsUtf8OrUtf16(string path, string value)
{
    var bytes = File.ReadAllBytes(path);
    return ContainsBytes(bytes, Encoding.UTF8.GetBytes(value))
        || ContainsBytes(bytes, Encoding.Unicode.GetBytes(value))
        || ContainsBytes(bytes, Encoding.BigEndianUnicode.GetBytes(value));
}

static bool ContainsBytes(byte[] haystack, byte[] needle)
{
    if (needle.Length == 0 || haystack.Length < needle.Length)
    {
        return false;
    }

    for (var i = 0; i <= haystack.Length - needle.Length; i++)
    {
        var matched = true;
        for (var j = 0; j < needle.Length; j++)
        {
            if (haystack[i + j] != needle[j])
            {
                matched = false;
                break;
            }
        }
        if (matched)
        {
            return true;
        }
    }
    return false;
}

sealed record KeyEnvelope
{
    public required string Format { get; init; }
    public required int FormatVersion { get; init; }
    public required Guid VaultId { get; init; }
    public required int KeyGeneration { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required KeySlot PasswordSlot { get; init; }
    public required KeySlot RecoverySlot { get; init; }
}

sealed class KeySlot
{
    public required string Kdf { get; init; }
    public required string SaltB64 { get; init; }
    public required string NonceB64 { get; init; }
    public required string WrappedMasterKeyB64 { get; init; }
}
