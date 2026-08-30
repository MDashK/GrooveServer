namespace GrooveServer.Crypto;

/// <summary>
/// A cifra de sessao do DJMAX Online: um stream cipher auto-sincronizante de 8 bytes
/// construido sobre TEA + MT19937.
///
/// Reversed de:
///   FUN_0049f92c @ 0x0049f92c  — construtor / key schedule
///   FUN_01189400 @ 0x01189400  — caminho de cifra (byte a byte)
///   FUN_01189100 @ 0x01189100  — caminho rapido alinhado a dword (mesma direcao)
///   FUN_0049f645 @ 0x0049f645  — caminho rapido alinhado a qword (mesma direcao)
///
/// Estado (offsets em dwords no objeto original):
///   [0],[1]      bloco corrente de 8 bytes (material de XOR + realimentacao)
///   [2],[3]      copia do bloco, usada como metade alta da chave TEA
///   [4],[5]      valor de encadeamento de 64 bits
///   [6],[7]      ponteiro corrente / fim do bloco
///   [0x27a..b]   8 bytes de keystream do MT
///   [0x27c]      ponteiro corrente no keystream
///
/// Por byte:
///   out = keystream[k] ^ bloco[p] ^ in
///   bloco[p] = (cifrar ? in : out)      // realimenta sempre com o PLAINTEXT
///
/// Na fronteira de bloco (8 bytes):
///   chaveTEA   = bloco || bloco          (o bloco de plaintext repetido -> 128 bits)
///   encadeado  = TEA_encrypt(encadeado, chaveTEA)
///   bloco      = encadeado
///   keystream  = proximos 8 bytes do MT19937
/// </summary>
public sealed class DjMaxCipher
{
    private readonly byte[] _block = new byte[8];
    private readonly byte[] _keystream = new byte[8];
    private readonly uint[] _teaKey = new uint[4];
    private readonly MersenneTwister _mt;

    private uint _chain0;
    private uint _chain1;
    private int _pos;

    /// <summary>
    /// Constroi a cifra. Corresponde a FUN_0049f92c(state, v0, v1, key, keyLen).
    /// </summary>
    /// <param name="chain0">metade baixa do valor de encadeamento inicial</param>
    /// <param name="chain1">metade alta do valor de encadeamento inicial</param>
    /// <param name="sessionKey">chave de sessao como words de 32 bits (8 words = 32 bytes)</param>
    public DjMaxCipher(uint chain0, uint chain1, ReadOnlySpan<uint> sessionKey)
    {
        _chain0 = chain0;
        _chain1 = chain1;
        WriteBlock(chain0, chain1);
        _pos = 0;

        _mt = new MersenneTwister();
        _mt.InitByArray(sessionKey);
        RefillKeystream();
    }

    /// <summary>Cifra o buffer no lugar.</summary>
    public void Encrypt(Span<byte> data)
    {
        for (int i = 0; i < data.Length; i++)
        {
            byte input = data[i];
            data[i] = (byte)(_keystream[_pos] ^ _block[_pos] ^ input);
            _block[_pos] = input;               // realimenta com plaintext (= entrada)
            if (++_pos == 8) AdvanceBlock();
        }
    }

    /// <summary>Decifra o buffer no lugar (inverso exato de <see cref="Encrypt"/>).</summary>
    public void Decrypt(Span<byte> data)
    {
        for (int i = 0; i < data.Length; i++)
        {
            byte output = (byte)(_keystream[_pos] ^ _block[_pos] ^ data[i]);
            data[i] = output;
            _block[_pos] = output;              // realimenta com plaintext (= saida)
            if (++_pos == 8) AdvanceBlock();
        }
    }

    private void AdvanceBlock()
    {
        // chave TEA = bloco de plaintext repetido (state[0..1] copiados para state[2..3])
        uint b0 = BitConverter.ToUInt32(_block, 0);
        uint b1 = BitConverter.ToUInt32(_block, 4);
        _teaKey[0] = b0; _teaKey[1] = b1;
        _teaKey[2] = b0; _teaKey[3] = b1;

        (_chain0, _chain1) = Tea.EncryptBlock(_chain0, _chain1, _teaKey);
        WriteBlock(_chain0, _chain1);

        _pos = 0;
        RefillKeystream();
    }

    private DjMaxCipher(DjMaxCipher other)
    {
        other._block.CopyTo(_block, 0);
        other._keystream.CopyTo(_keystream, 0);
        other._teaKey.CopyTo(_teaKey, 0);
        _chain0 = other._chain0;
        _chain1 = other._chain1;
        _pos = other._pos;
        _mt = other._mt.Clone();
    }

    /// <summary>Copia independente do estado, para explorar alternativas com backtracking.</summary>
    public DjMaxCipher Clone() => new(this);

    private void WriteBlock(uint lo, uint hi)
    {
        BitConverter.TryWriteBytes(_block.AsSpan(0, 4), lo);
        BitConverter.TryWriteBytes(_block.AsSpan(4, 4), hi);
    }

    private void RefillKeystream()
    {
        ulong ks = _mt.NextUInt64();
        BitConverter.TryWriteBytes(_keystream, ks);
    }
}
