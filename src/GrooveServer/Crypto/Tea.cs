namespace GrooveServer.Crypto;

/// <summary>
/// TEA (Tiny Encryption Algorithm) tal como implementado no cliente DJMAX.
/// Reversed de FUN_0049f571 @ 0x0049f571 no dump desempacotado do DJMax.client.
///
/// O cliente usa a variante canonica: 32 rondas, delta 0x9E3779B9, chave de 128 bits.
/// No decompile o delta aparece como -0x61C88647, que e' exatamente 0x9E3779B9 em
/// aritmetica de 32 bits com sinal.
///
/// Original (Ghidra):
///   iVar3 = iVar3 + -0x61c88647;
///   uVar1 = uVar1 + ((uVar2 >> 5) + k[1] ^ uVar2 * 0x10 + k[0] ^ iVar3 + uVar2);
///   uVar2 = uVar2 + ((uVar1 >> 5) + k[3] ^ uVar1 * 0x10 + k[2] ^ iVar3 + uVar1);
///
/// Nota: `uVar2 * 0x10` e' `v1 << 4` e `uVar2 >> 5` e' um shift logico (unsigned).
/// </summary>
public static class Tea
{
    public const uint Delta = 0x9E3779B9;
    public const int Rounds = 32;

    /// <summary>Encripta um bloco de 64 bits (v0, v1) com a chave de 128 bits.</summary>
    public static (uint V0, uint V1) EncryptBlock(uint v0, uint v1, ReadOnlySpan<uint> key)
    {
        if (key.Length < 4)
            throw new ArgumentException("A chave TEA precisa de 4 words de 32 bits.", nameof(key));

        uint sum = 0;
        for (int i = 0; i < Rounds; i++)
        {
            sum += Delta;
            v0 += ((v1 >> 5) + key[1]) ^ ((v1 << 4) + key[0]) ^ (sum + v1);
            v1 += ((v0 >> 5) + key[3]) ^ ((v0 << 4) + key[2]) ^ (sum + v0);
        }
        return (v0, v1);
    }

    /// <summary>
    /// Desencripta um bloco de 64 bits. Nao e' usado pelo modo de stream do cliente
    /// (que so' aplica a direcao "encrypt" ao valor de encadeamento), mas fica aqui
    /// para completude e para testes de ida-e-volta.
    /// </summary>
    public static (uint V0, uint V1) DecryptBlock(uint v0, uint v1, ReadOnlySpan<uint> key)
    {
        if (key.Length < 4)
            throw new ArgumentException("A chave TEA precisa de 4 words de 32 bits.", nameof(key));

        uint sum = unchecked(Delta * Rounds);
        for (int i = 0; i < Rounds; i++)
        {
            v1 -= ((v0 >> 5) + key[3]) ^ ((v0 << 4) + key[2]) ^ (sum + v0);
            v0 -= ((v1 >> 5) + key[1]) ^ ((v1 << 4) + key[0]) ^ (sum + v1);
            sum -= Delta;
        }
        return (v0, v1);
    }
}
