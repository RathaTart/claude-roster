using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

namespace ClaudeRoster.Core;

internal static class AtomicFile
{
    /// <summary>Writes to a sibling temp file, then swaps it in, so readers never see a half-written file.</summary>
    public static void WriteAllText(string path, string contents)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)!;
        Directory.CreateDirectory(directory);
        var temp = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
            {
                writer.Write(contents);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            FileOps.Retry(() => File.Move(temp, fullPath, overwrite: true));
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }
}

internal static class FileOps
{
    private const int Attempts = 8;

    public static void DeleteDirectory(string directory) => Retry(() =>
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(directory, recursive: true);
    });

    public static void MoveDirectory(string source, string destination) => Retry(() => Directory.Move(source, destination));

    public static void DeleteFile(string path) => Retry(() =>
    {
        if (File.Exists(path))
        {
            File.SetAttributes(path, FileAttributes.Normal);
            File.Delete(path);
        }
    });

    /// <summary>
    /// Antivirus and indexers briefly hold freshly written files open, which makes renames and deletes
    /// fail with access denied for a moment. Retrying with a short backoff rides that out.
    /// </summary>
    public static void Retry(Action operation)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                operation();
                return;
            }
            catch (Exception exception) when (attempt < Attempts && exception is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(25 * attempt * attempt);
            }
        }
    }
}

/// <summary>Limits a folder to the current user, SYSTEM and Administrators; saved sign-ins live under it.</summary>
internal static class SecureDirectory
{
    public static void Ensure(string path)
    {
        var directory = Directory.CreateDirectory(path);
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        var inheritance = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        foreach (var sid in new[]
                 {
                     WindowsIdentity.GetCurrent().User!,
                     new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                     new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                 })
        {
            security.AddAccessRule(new FileSystemAccessRule(
                sid, FileSystemRights.FullControl, inheritance, PropagationFlags.None, AccessControlType.Allow));
        }

        directory.SetAccessControl(security);
    }
}
