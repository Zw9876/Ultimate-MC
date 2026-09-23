using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace MinecraftLauncher.Core
{
    /// <summary>
    /// The RSA key the skin server signs texture data with.
    /// </summary>
    /// <remarks>
    /// The key is persisted and must stay stable: authlib-injector clients trust
    /// the public key they were handed, so regenerating it invalidates every
    /// client that already cached the old one. The file keeps .NET's RSA XML
    /// format so keys written by the PowerShell launcher keep working.
    /// </remarks>
    public static class SkinKey
    {
        public static string KeyFile => Path.Combine(SkinStore.Dir, "skinserver_rsa.xml");

        public static RSA LoadOrCreate(string? keyFile = null)
        {
            string path = keyFile ?? KeyFile;
            var rsa = RSA.Create(2048);

            if (File.Exists(path))
            {
                try
                {
                    rsa.FromXmlString(File.ReadAllText(path));
                    return rsa;
                }
                catch
                {
                    // Unreadable key file — fall through and replace it. Clients
                    // that cached the old public key will need to reconnect.
                    rsa.Dispose();
                    rsa = RSA.Create(2048);
                }
            }

            Save(rsa, path);
            return rsa;
        }

        public static void Save(RSA rsa, string? keyFile = null)
        {
            string path = keyFile ?? KeyFile;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, rsa.ToXmlString(includePrivateParameters: true), new UTF8Encoding(false));
        }

        /// <summary>
        /// SubjectPublicKeyInfo PEM for the authlib-injector metadata document.
        /// The trailing newline matches what the PowerShell server emitted.
        /// </summary>
        public static string PublicKeyPem(RSA rsa) =>
            rsa.ExportSubjectPublicKeyInfoPem().ReplaceLineEndings("\n") + "\n";
    }
}
