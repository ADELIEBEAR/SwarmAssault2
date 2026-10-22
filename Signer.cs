// SPDX-License-Identifier: MIT
using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using SwarmDistribution;
class Signer
{
    static int Main(string[] args)
    {
        try
        {
            if (args.Length == 3 && args[0] == "generate")
            {
                if (File.Exists(args[1]) || File.Exists(args[2])) throw new IOException("Refusing to overwrite an existing signing key.");
                using (var rsa = new RSACryptoServiceProvider(3072)) { rsa.PersistKeyInCsp = false; File.WriteAllText(args[1], rsa.ToXmlString(true)); File.WriteAllText(args[2], rsa.ToXmlString(false)); }
            }
            else if (args.Length == 4 && args[0] == "sign")
            {
                byte[] payload = File.ReadAllBytes(args[2]);
                using (var rsa = new RSACryptoServiceProvider())
                {
                    rsa.PersistKeyInCsp = false; rsa.FromXmlString(File.ReadAllText(args[1]));
                    byte[] signature = rsa.SignData(payload, CryptoConfig.MapNameToOID("SHA256"));
                    File.WriteAllText(args[3], Core.Json.Serialize(new Signed { Payload = Convert.ToBase64String(payload), Signature = Convert.ToBase64String(signature) }), new UTF8Encoding(false));
                }
            }
            else throw new ArgumentException("generate private.xml public.xml | sign private.xml payload.json swarm-update.json");
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e.Message); return 1; }
    }
}
