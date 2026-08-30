namespace GrooveServer.Crypto;

/// <summary>
/// MT19937 (Mersenne Twister) tal como implementado no cliente DJMAX.
/// Reversed de FUN_0049e34b (genrand), FUN_0049e25e (init_genrand)
/// e FUN_0049e2a4 (init_by_array) no dump desempacotado.
///
/// E' a implementacao de referencia de Matsumoto/Nishimura, sem alteracoes:
///   - init_genrand com multiplicador 0x6C078965
///   - init_by_array semeado com 19650218 (0x12BD6AA) e multiplicadores
///     0x19660D / 0x5D588B65
///   - twist com mascara 0x7FFFFFFF e mag01 = { 0, 0x9908B0DF }
///   - tempering 11 / 7 (0x9D2C5680) / 15 (0xEFC60000) / 18
///
/// Nota sobre o decompile: as mascaras de tempering aparecem no Ghidra como
/// 0xff3a58ad e 0xffffdf8c porque o compilador reescreveu `(x << 7) & 0x9D2C5680`
/// como `(x & 0xff3a58ad) << 7` (mesma coisa: 0x9D2C5680 >> 7 == 0x13A58AD, com os
/// bits altos irrelevantes por serem descartados pelo shift). O comportamento e'
/// identico ao MT19937 canonico.
/// </summary>
public sealed class MersenneTwister
{
    private const int N = 624;          // 0x270
    private const int M = 397;          // 0x18D
    private const uint MatrixA = 0x9908B0DF;
    private const uint UpperMask = 0x80000000;
    private const uint LowerMask = 0x7FFFFFFF;

    private readonly uint[] _mt = new uint[N];
    private int _mti = N + 1;

    public MersenneTwister() { }

    public MersenneTwister(uint seed) => InitGenrand(seed);

    public MersenneTwister(ReadOnlySpan<uint> initKey) => InitByArray(initKey);

    /// <summary>Copia independente do estado do gerador.</summary>
    public MersenneTwister Clone()
    {
        var c = new MersenneTwister();
        _mt.CopyTo(c._mt, 0);
        c._mti = _mti;
        return c;
    }

    /// <summary>init_genrand — FUN_0049e25e.</summary>
    public void InitGenrand(uint s)
    {
        _mt[0] = s;
        for (_mti = 1; _mti < N; _mti++)
        {
            uint prev = _mt[_mti - 1];
            _mt[_mti] = unchecked(0x6C078965u * (prev ^ (prev >> 30)) + (uint)_mti);
        }
    }

    /// <summary>init_by_array — FUN_0049e2a4. E' assim que a chave de sessao entra no gerador.</summary>
    public void InitByArray(ReadOnlySpan<uint> initKey)
    {
        InitGenrand(19650218u);          // 0x12BD6AA
        int i = 1, j = 0;
        int k = Math.Max(N, initKey.Length);

        for (; k > 0; k--)
        {
            uint prev = _mt[i - 1];
            _mt[i] = unchecked((_mt[i] ^ ((prev ^ (prev >> 30)) * 0x19660Du)) + initKey[j] + (uint)j);
            i++; j++;
            if (i >= N) { _mt[0] = _mt[N - 1]; i = 1; }
            if (j >= initKey.Length) j = 0;
        }

        for (k = N - 1; k > 0; k--)
        {
            uint prev = _mt[i - 1];
            _mt[i] = unchecked((_mt[i] ^ ((prev ^ (prev >> 30)) * 0x5D588B65u)) - (uint)i);
            i++;
            if (i >= N) { _mt[0] = _mt[N - 1]; i = 1; }
        }

        _mt[0] = 0x80000000u;
    }

    /// <summary>genrand_int32 — FUN_0049e34b.</summary>
    public uint NextUInt32()
    {
        uint y;

        if (_mti >= N)
        {
            if (_mti == N + 1)
                InitGenrand(5489u);      // 0x1571 no decompile

            int kk;
            for (kk = 0; kk < N - M; kk++)
            {
                y = (_mt[kk] & UpperMask) | (_mt[kk + 1] & LowerMask);
                _mt[kk] = _mt[kk + M] ^ (y >> 1) ^ ((y & 1) != 0 ? MatrixA : 0u);
            }
            for (; kk < N - 1; kk++)
            {
                y = (_mt[kk] & UpperMask) | (_mt[kk + 1] & LowerMask);
                _mt[kk] = _mt[kk + (M - N)] ^ (y >> 1) ^ ((y & 1) != 0 ? MatrixA : 0u);
            }
            y = (_mt[N - 1] & UpperMask) | (_mt[0] & LowerMask);
            _mt[N - 1] = _mt[M - 1] ^ (y >> 1) ^ ((y & 1) != 0 ? MatrixA : 0u);

            _mti = 0;
        }

        y = _mt[_mti++];

        // tempering
        y ^= y >> 11;
        y ^= (y << 7) & 0x9D2C5680u;
        y ^= (y << 15) & 0xEFC60000u;
        y ^= y >> 18;
        return y;
    }

    /// <summary>
    /// Devolve 8 bytes de keystream (dois genrand consecutivos, low word primeiro).
    /// Corresponde a FUN_0049f629, que faz CONCAT44(segunda, primeira).
    /// </summary>
    public ulong NextUInt64()
    {
        uint lo = NextUInt32();
        uint hi = NextUInt32();
        return ((ulong)hi << 32) | lo;
    }
}
