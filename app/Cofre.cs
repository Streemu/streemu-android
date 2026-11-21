using Android.Security.Keystore;
using Java.Security;
using Javax.Crypto;
using Javax.Crypto.Spec;

namespace Streemu.Proto;

/// <summary>
/// Cifra o segredo da conta com uma chave AES-GCM do Android Keystore. A chave nunca sai do aparelho (nem no backup), então o arquivo da conta copiado para
/// outro lugar não abre. Formato: 12 bytes de IV + texto cifrado com a etiqueta.
/// </summary>
static class Cofre
{
    const string Alias = "streemu-conta-v1";

    static IKey Chave()
    {
        var ks = KeyStore.GetInstance("AndroidKeyStore")!;
        ks.Load(null);
        if (ks.GetKey(Alias, null) is IKey k) return k;
        var gen = KeyGenerator.GetInstance(KeyProperties.KeyAlgorithmAes, "AndroidKeyStore")!;
        gen.Init(new KeyGenParameterSpec.Builder(Alias, KeyStorePurpose.Encrypt | KeyStorePurpose.Decrypt)
            .SetBlockModes(KeyProperties.BlockModeGcm)
            .SetEncryptionPaddings(KeyProperties.EncryptionPaddingNone)
            .SetKeySize(256)
            .Build());
        return gen.GenerateKey()!;
    }

    public static byte[] Cifrar(byte[] claro)
    {
        var c = Cipher.GetInstance("AES/GCM/NoPadding")!;
        c.Init(CipherMode.EncryptMode, Chave());
        var iv = c.GetIV()!;
        var cifrado = c.DoFinal(claro)!;
        return iv.Concat(cifrado).ToArray();
    }

    public static byte[] Abrir(byte[] dados)
    {
        var c = Cipher.GetInstance("AES/GCM/NoPadding")!;
        c.Init(CipherMode.DecryptMode, Chave(), new GCMParameterSpec(128, dados[..12]));
        return c.DoFinal(dados[12..])!;
    }
}
