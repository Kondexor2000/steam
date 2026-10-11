using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace ServerActivity.Api.Authentication;

public interface ISecurityBackupService
{
    bool HasBackup { get; }
    void CreateEncryptedBackup();
    byte[] GetDecryptedBackup();
}

public sealed class SecurityBackupService(
    IWebHostEnvironment environment,
    IConfiguration configuration) : ISecurityBackupService
{
    private static readonly byte[] Header = Encoding.ASCII.GetBytes("SAB1");
    private const int NonceLength = 12;
    private const int TagLength = 16;

    private readonly string _backupDirectory = Path.GetFullPath(
        configuration["Security:BackupDirectory"] ??
        Path.Combine(environment.ContentRootPath, "data", "secure_backups"));
    private readonly string _backupPath = Path.GetFullPath(Path.Combine(
        configuration["Security:BackupDirectory"] ??
        Path.Combine(environment.ContentRootPath, "data", "secure_backups"),
        "backup.enc"));
    private readonly (string Name, string Path)[] _databases =
    [
        (
            "users.db",
            GetDatabasePath(
                configuration["Authentication:DatabasePath"],
                environment.ContentRootPath,
                "users.db")),
        (
            "playtime.db",
            GetDatabasePath(
                configuration["LocalPlaytime:DatabasePath"],
                environment.ContentRootPath,
                "playtime.db")),
    ];
    private readonly string _dataProtectionKeysPath = Path.Combine(
        Path.GetDirectoryName(GetDatabasePath(
            configuration["LocalPlaytime:DatabasePath"],
            environment.ContentRootPath,
            "playtime.db"))!,
        "keys");

    public bool HasBackup => File.Exists(_backupPath);

    public void CreateEncryptedBackup()
    {
        string? temporaryDirectory = null;
        string? temporaryBackupPath = null;
        try
        {
            var key = GetKey();
            Directory.CreateDirectory(_backupDirectory);
            temporaryDirectory = Path.Combine(
                _backupDirectory,
                $"snapshot-{Guid.NewGuid():N}");
            Directory.CreateDirectory(temporaryDirectory);

            var plaintext = CreateArchive(temporaryDirectory);
            var encrypted = Encrypt(plaintext, key);
            temporaryBackupPath = _backupPath + $".{Guid.NewGuid():N}.tmp";
            File.WriteAllBytes(temporaryBackupPath, encrypted);
            File.Move(temporaryBackupPath, _backupPath, overwrite: true);
            temporaryBackupPath = null;
        }
        catch (SecureBackupException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or
                UnauthorizedAccessException or
                SqliteException or
                CryptographicException or
                InvalidOperationException or
                FormatException)
        {
            throw new SecureBackupException(
                "Nie udało się utworzyć zaszyfrowanej kopii zapasowej.",
                exception);
        }
        finally
        {
            if (temporaryBackupPath is not null && File.Exists(temporaryBackupPath))
            {
                File.Delete(temporaryBackupPath);
            }

            if (temporaryDirectory is not null && Directory.Exists(temporaryDirectory))
            {
                Directory.Delete(temporaryDirectory, recursive: true);
            }
        }
    }

    public byte[] GetDecryptedBackup()
    {
        try
        {
            var encrypted = File.ReadAllBytes(_backupPath);
            return Decrypt(encrypted, GetKey());
        }
        catch (SecureBackupException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or
                UnauthorizedAccessException or
                CryptographicException or
                InvalidOperationException or
                FormatException)
        {
            throw new SecureBackupException(
                "Nie udało się odczytać lub zweryfikować zaszyfrowanej kopii zapasowej.",
                exception);
        }
    }

    private byte[] CreateArchive(string temporaryDirectory)
    {
        using var archiveStream = new MemoryStream();
        using (var archive = new ZipArchive(archiveStream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, databasePath) in _databases)
            {
                if (!File.Exists(databasePath))
                {
                    throw new SecureBackupException(
                        $"Nie znaleziono bazy danych wymaganej do utworzenia kopii: {name}.");
                }

                var snapshotPath = Path.Combine(temporaryDirectory, name);
                CreateDatabaseSnapshot(databasePath, snapshotPath);
                var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
                using var entryStream = entry.Open();
                using var databaseStream = File.OpenRead(snapshotPath);
                databaseStream.CopyTo(entryStream);
            }

            if (Directory.Exists(_dataProtectionKeysPath))
            {
                foreach (var keyPath in Directory.GetFiles(_dataProtectionKeysPath, "*", SearchOption.TopDirectoryOnly))
                {
                    var entry = archive.CreateEntry($"data-protection-keys/{Path.GetFileName(keyPath)}", CompressionLevel.Optimal);
                    using var entryStream = entry.Open();
                    using var keyStream = File.OpenRead(keyPath);
                    keyStream.CopyTo(entryStream);
                }
            }
        }

        return archiveStream.ToArray();
    }

    private static void CreateDatabaseSnapshot(string sourcePath, string destinationPath)
    {
        using var source = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = sourcePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        using var destination = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = destinationPath,
            Pooling = false,
        }.ToString());
        source.Open();
        destination.Open();
        source.BackupDatabase(destination);
    }

    private static byte[] Encrypt(byte[] plaintext, byte[] key)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceLength);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagLength];
        using var aes = new AesGcm(key, TagLength);
        aes.Encrypt(nonce, plaintext, ciphertext, tag, Header);

        var encrypted = new byte[Header.Length + nonce.Length + tag.Length + ciphertext.Length];
        Buffer.BlockCopy(Header, 0, encrypted, 0, Header.Length);
        Buffer.BlockCopy(nonce, 0, encrypted, Header.Length, nonce.Length);
        Buffer.BlockCopy(tag, 0, encrypted, Header.Length + nonce.Length, tag.Length);
        Buffer.BlockCopy(ciphertext, 0, encrypted, Header.Length + nonce.Length + tag.Length, ciphertext.Length);
        return encrypted;
    }

    private static byte[] Decrypt(byte[] encrypted, byte[] key)
    {
        var minimumLength = Header.Length + NonceLength + TagLength;
        if (encrypted.Length < minimumLength ||
            !encrypted.AsSpan(0, Header.Length).SequenceEqual(Header))
        {
            throw new SecureBackupException("Plik kopii zapasowej ma nieprawidłowy format.");
        }

        var nonce = encrypted.AsSpan(Header.Length, NonceLength);
        var tag = encrypted.AsSpan(Header.Length + NonceLength, TagLength);
        var ciphertext = encrypted.AsSpan(minimumLength);
        var plaintext = new byte[ciphertext.Length];
        using var aes = new AesGcm(key, TagLength);
        aes.Decrypt(nonce, ciphertext, tag, plaintext, Header);
        return plaintext;
    }

    private byte[] GetKey()
    {
        var configuredKey = configuration["Security:BackupKey"];
        if (string.IsNullOrWhiteSpace(configuredKey))
        {
            throw new SecureBackupException(
                "Brak klucza Security:BackupKey. Skonfiguruj trwały klucz Base64 o długości 32 bajtów.");
        }

        byte[] key;
        try
        {
            key = Convert.FromBase64String(configuredKey);
        }
        catch (FormatException exception)
        {
            throw new SecureBackupException("Klucz Security:BackupKey nie jest poprawnym Base64.", exception);
        }

        if (key.Length != 32)
        {
            throw new SecureBackupException("Klucz Security:BackupKey musi mieć dokładnie 32 bajty.");
        }

        return key;
    }

    private static string GetDatabasePath(
        string? configuredPath,
        string contentRootPath,
        string defaultFileName)
    {
        var path = configuredPath ?? Path.Combine(contentRootPath, "data", defaultFileName);
        return Path.GetFullPath(path);
    }
}

public sealed class SecureBackupException(string message, Exception? innerException = null)
    : Exception(message, innerException);
