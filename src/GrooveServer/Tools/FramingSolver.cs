namespace GrooveServer.Tools;

/// <summary>
/// Recupera o enquadramento dos pacotes a partir de um stream capturado, sem
/// precisar de decifrar nada.
///
/// A chave e' que os 7 bytes de cabecalho de cada pacote NUNCA sao cifrados
/// (o cliente so' cifra a partir do offset 7: FUN_0043a420 chama
/// encrypt(buf+7, len-7)). Logo, o message id de cada pacote esta' em claro no
/// stream, no seu offset de inicio.
///
/// Assim, encontrar o enquadramento reduz-se a encontrar a particao do stream em
/// que cada fronteira cai num message id conhecido. Com 116 ids validos num
/// espaco de 65536, um falso positivo por acaso e' raro, e exigir que a particao
/// cubra o stream INTEIRO torna a solucao praticamente unica.
/// </summary>
public static class FramingSolver
{
    /// <summary>Ids validos servidor -&gt; cliente, extraidos do dispatcher FUN_004307c0.</summary>
    public static readonly HashSet<ushort> ServerToClientIds = new(new ushort[]
    {
        0x03,0x07,0x08,0x09,0x0A,0x0B,0x0C,0x10,0x12,0x14,0x16,0x18,0x1A,0x1E,0x1F,
        0x20,0x22,0x24,0x25,0x26,0x27,0x28,0x29,0x2A,0x2B,0x2C,0x2D,0x2E,0x2F,0x31,
        0x33,0x35,0x37,0x39,0x3A,0x3B,0x3C,0x3D,0x40,0x41,0x42,0x43,0x44,0x45,0x47,
        0x48,0x4D,0x50,0x51,0x54,0x55,0x59,0x5B,0x5E,0x60,0x61,0x63,0x65,0x68,0x6B,
        0x6D,0x70,0x71,0x74,0x77,0x78,0x7A,0x7C,0x82,0x84,0x86,0x88,0x89,0x8C,0x97,
        0x9A,0x9D,0xA1,0xA2,0xA7,0xB3,0xB5,0xB6,0xB8,0xB9,0xBB,0xBC,0xBD,0xBE,0xC0,
        0xC4,0xC6,0xC9,0xCA,0xD2,0xD3,0xD4,0xD8,0xDA,0xDC,0xDE,0xE0,0xE4,0xE5,0xE6,
        0xF1,0xF3,0xF4,0xF5,0xF6,0xFB,0xFC,0xFE,0x10E,0x12D,0x12E,0x130,
    });

    private const int MinPacket = 3;
    private const int MaxPacket = 4096;

    /// <summary>
    /// Devolve os comprimentos dos pacotes, pela ordem do stream, ou null se nao
    /// existir particao consistente.
    /// </summary>
    public static List<int>? Solve(byte[] stream, ISet<ushort> validIds)
    {
        // memo[p] = 0 desconhecido, 1 conduz ao fim, 2 nao conduz
        var memo = new byte[stream.Length + 1];
        var choice = new int[stream.Length + 1];

        bool Search(int p)
        {
            if (p == stream.Length) return true;
            if (p + 2 > stream.Length) return false;
            if (memo[p] != 0) return memo[p] == 1;

            memo[p] = 2;   // marca como "em curso"/falha, evita ciclos

            ushort id = BitConverter.ToUInt16(stream, p);
            if (!validIds.Contains(id)) return false;

            for (int len = MinPacket; len <= MaxPacket; len++)
            {
                int next = p + len;
                if (next > stream.Length) break;
                if (next < stream.Length && next + 2 > stream.Length) continue;
                if (next < stream.Length && !validIds.Contains(BitConverter.ToUInt16(stream, next)))
                    continue;
                if (Search(next))
                {
                    memo[p] = 1;
                    choice[p] = len;
                    return true;
                }
            }
            return false;
        }

        if (!Search(0)) return null;

        var result = new List<int>();
        for (int p = 0; p < stream.Length; p += choice[p]) result.Add(choice[p]);
        return result;
    }

    /// <summary>
    /// Versao com dois criterios: fronteiras em message ids validos E corpos que
    /// decifram para algo plausivel.
    ///
    /// O segundo criterio e' o que desempata. Dados reais tem muito 0x00 e 0xCC
    /// (enchimento) e strings ASCII; uma fronteira errada dessincroniza a cifra e
    /// produz bytes praticamente aleatorios, onde 0x00/0xCC aparecem em ~0,8% dos
    /// casos. O limiar esta' deliberadamente baixo para nao excluir corpos densos.
    /// </summary>
    public static List<int>? SolveWithCipher(
        byte[] stream, ISet<ushort> validIds, int startOffset, Crypto.DjMaxCipher cipher)
    {
        var result = new List<int>();

        bool Search(int p, Crypto.DjMaxCipher state, int depth)
        {
            if (p == stream.Length) return true;
            if (p + 2 > stream.Length) return false;
            if (depth > 512) return false;
            if (!validIds.Contains(BitConverter.ToUInt16(stream, p))) return false;

            for (int len = MinPacket; len <= MaxPacket; len++)
            {
                int next = p + len;
                if (next > stream.Length) break;
                // criterio 1 (barato): a fronteira seguinte tem de ser um msgid valido
                if (next < stream.Length &&
                    (next + 2 > stream.Length || !validIds.Contains(BitConverter.ToUInt16(stream, next))))
                    continue;

                // criterio 2: o corpo tem de decifrar para algo plausivel
                var probe = state.Clone();
                if (len >= 8)
                {
                    var body = stream.AsSpan(p + 7, len - 7).ToArray();
                    probe.Decrypt(body);
                    if (!LooksPlausible(body)) continue;
                }

                result.Add(len);
                if (Search(next, probe, depth + 1)) return true;
                result.RemoveAt(result.Count - 1);
            }
            return false;
        }

        return Search(startOffset, cipher, 0) ? result : null;
    }

    /// <summary>Heuristica de plausibilidade de um corpo decifrado.</summary>
    private static bool LooksPlausible(byte[] body)
    {
        if (body.Length < 12) return true;          // curto de mais para julgar
        int filler = body.Count(b => b == 0x00 || b == 0xCC);
        double ratio = (double)filler / body.Length;
        return ratio >= 0.12;                        // aleatorio daria ~0,008
    }

    public static void Run(string path, string direction)
    {
        // Reconstitui o fluxo continuo de uma direcao a partir do ficheiro de pacotes.
        var chunks = File.ReadAllLines(path)
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(l => l.Split('\t'))
            .Where(p => p.Length >= 3 && p[0] == direction)
            .Select(p => Convert.FromHexString(p[2]))
            .ToList();

        var stream = chunks.SelectMany(c => c).ToArray();
        Console.WriteLine($"{direction}: {chunks.Count} segmentos TCP, {stream.Length} bytes no total");

        // Ancorar no que sabemos com certeza: hello de 3 bytes, depois ConnectAck de
        // 47 bytes, que entrega a chave de sessao. So' a partir dai' e' que ha' cifra.
        const int helloLen = 3, connectAckLen = 47;
        int anchor = helloLen + connectAckLen;
        if (stream.Length < anchor || BitConverter.ToUInt16(stream, helloLen) != 0x000A)
        {
            Console.WriteLine("Stream nao comeca com hello + ConnectAck; nao da' para ancorar.");
            return;
        }

        var key = Protocol.PacketCodec.TransformSessionKey(stream.AsSpan(helloLen + 7, 32));
        var words = new uint[8];
        for (int w = 0; w < 8; w++) words[w] = BitConverter.ToUInt32(key, w * 4);
        var cipher = new Crypto.DjMaxCipher(BitConverter.ToUInt16(key, 0x1C), 0u, words);
        Console.WriteLine($"Chave de sessao: {Convert.ToHexString(key)}");

        var tail = FramingSolver.SolveWithCipher(stream, ServerToClientIds, anchor, cipher);
        if (tail is null)
        {
            Console.WriteLine("Nao foi encontrada nenhuma particao consistente com a decifra.");
            return;
        }
        var lengths = new List<int> { helloLen, connectAckLen };
        lengths.AddRange(tail);

        Console.WriteLine($"\nEnquadramento recuperado: {lengths.Count} pacotes logicos\n");
        Console.WriteLine("  #   offset    len   msgid");
        int off = 0, i = 0;
        var sizesById = new Dictionary<ushort, SortedSet<int>>();
        foreach (var len in lengths)
        {
            ushort id = BitConverter.ToUInt16(stream, off);
            Console.WriteLine($"  {i,-3} 0x{off:x4}  {len,5}   0x{id:x4} ({id})");
            if (!sizesById.TryGetValue(id, out var set)) sizesById[id] = set = new SortedSet<int>();
            set.Add(len);
            off += len; i++;
        }

        Console.WriteLine("\nTamanhos observados por msgid:");
        foreach (var kv in sizesById.OrderBy(k => k.Key))
            Console.WriteLine($"  0x{kv.Key:x4} ({kv.Key,3}) -> {string.Join(", ", kv.Value)}" +
                              (kv.Value.Count > 1 ? "   <- TAMANHO VARIAVEL" : ""));
    }
}
