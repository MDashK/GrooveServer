using System.Globalization;
using GrooveServer.Crypto;
using GrooveServer.Protocol;

namespace GrooveServer.Tools;

/// <summary>
/// Descobre a tabela de tamanhos servidor -&gt; cliente sem busca, explorando o facto
/// de a cifra denunciar as fronteiras.
///
/// Para cada pacote de tamanho desconhecido: decifra-se em continuo para la' do seu
/// fim provavel. Enquanto se esta' dentro do pacote o texto tem muito enchimento
/// (0x00/0xCC); assim que se atravessa a fronteira, os 7 bytes de cabecalho do pacote
/// seguinte — nunca cifrados — entram na cifra, o estado dessincroniza e o enchimento
/// desaba para o nivel de ruido. O ponto de colapso e' a fronteira.
///
/// Localiza-se o colapso por janela deslizante e depois refina-se: a fronteira exata
/// tem de ser um offset onde os dois bytes seguintes formem um message id valido.
/// </summary>
public static class AutoFramer
{
    private const int Window = 16;
    private const double FillerFloor = 0.10;
    // A lista de musicas vem numa mensagem enorme (o servidor envia ~18 KB de uma vez
    // ao entrar na sala), por isso o limite tem de ser generoso.
    private const int MaxPacket = 40960;

    /// <summary>Texto decifrado acumulado, para verificar contra factos conhecidos.</summary>
    private static readonly List<byte> decrypted = new();

    /// <summary>Imprime os candidatos a fronteira e as respetivas pontuacoes.</summary>
    public static bool Diagnose { get; set; }

    public static void Run(string playerName, params string[] paths)
    {
        var sizes = new Dictionary<ushort, int>(BurstSolver.Known);
        var report = new List<string>();

        foreach (var path in paths)
        {
            if (!File.Exists(path)) continue;
            Console.WriteLine($"\n########## {Path.GetFileName(path)} ##########");
            ProcessStream(path, sizes, report);
        }

        Console.WriteLine("\n=== TABELA S2C DESCOBERTA ===");
        foreach (var (id, len) in sizes.OrderBy(k => k.Key))
            Console.WriteLine($"  0x{id:x4} ({id,3}) = {len,5}  {SizeHarvester.MessageNames.GetValueOrDefault(id, "")}");
        Console.WriteLine($"\ntotal: {sizes.Count} mensagens");

        // === Verificacao contra factos conhecidos ===
        // Se o enquadramento estiver certo, o texto decifrado tem de conter o nome do
        // jogador e os valores que ele ve' no ecra. Se estiver errado, nao contem nada.
        Console.WriteLine("\n=== VERIFICACAO ===");
        var all = decrypted.ToArray();
        Console.WriteLine($"total decifrado: {all.Length} bytes");
        Check(all, "nome ASCII", System.Text.Encoding.ASCII.GetBytes(playerName));
        Check(all, "nome UTF-16", System.Text.Encoding.Unicode.GetBytes(playerName));
        Check(all, "MAX 15951 (LE32)", BitConverter.GetBytes(15951));
        Check(all, "MAX 16022 (LE32)", BitConverter.GetBytes(16022));
        Check(all, "level 8 (LE32)", BitConverter.GetBytes(8));
        Check(all, "level 9 (LE32)", BitConverter.GetBytes(9));

        Console.WriteLine("\n=== CORPOS DECIFRADOS ===");
        foreach (var line in report.Take(40)) Console.WriteLine("  " + line);
    }

    private static void Check(byte[] hay, string label, byte[] needle)
    {
        var hits = new List<int>();
        for (int i = 0; i + needle.Length <= hay.Length && hits.Count < 6; i++)
        {
            bool ok = true;
            for (int j = 0; j < needle.Length; j++) if (hay[i + j] != needle[j]) { ok = false; break; }
            if (ok) hits.Add(i);
        }
        Console.WriteLine($"  {label,-20}: " +
            (hits.Count > 0 ? $"ENCONTRADO em {string.Join(", ", hits.Select(h => $"0x{h:x}"))}" : "nao encontrado"));
    }

    private static string Ascii(byte[] d, int max) =>
        new(d.Take(max).Select(b => b >= 0x20 && b <= 0x7E ? (char)b : '.').ToArray());

    private static void ProcessStream(string path, Dictionary<ushort, int> sizes, List<string> report)
    {
        var stream = File.ReadAllLines(path)
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(l => l.Split('\t'))
            .Where(p => p.Length >= 3 && p[0] == "S2C" && p[2].Length > 0)
            .SelectMany(p => Convert.FromHexString(p[2]))
            .ToArray();

        if (stream.Length < 50 || BitConverter.ToUInt16(stream, 3) != 0x000A)
        { Console.WriteLine("stream nao comeca com hello + ConnectAck; ignorado"); return; }

        var key = PacketCodec.TransformSessionKey(stream.AsSpan(3 + 7, 32));
        var words = new uint[8];
        for (int w = 0; w < 8; w++) words[w] = BitConverter.ToUInt32(key, w * 4);
        var cipher = new DjMaxCipher(BitConverter.ToUInt16(key, 0x1C), 0u, words);

        int pos = 3 + 47;   // depois do hello e do ConnectAck
        while (pos + 2 <= stream.Length)
        {
            ushort id = BitConverter.ToUInt16(stream, pos);
            if (id is 0 or >= 0x400)
            { Console.WriteLine($"  0x{pos:x4}: msgid 0x{id:x4} fora do intervalo — paragem"); break; }
            if (!FramingSolver.ServerToClientIds.Contains(id))
                Console.WriteLine($"  0x{pos:x4}: msgid 0x{id:x4} nao esta' no dispatcher extraido (continuo)");

            // O GameInfoInf tem tamanho variavel, com o comprimento cifrado no corpo.
            if (id == Protocol.GameInfoFraming.MessageId)
            {
                int total;
                try { total = Protocol.GameInfoFraming.ReadTotalLength(stream, pos, cipher, out var prefix); }
                catch (Exception ex) { Console.WriteLine($"  0x{pos:x4}: GameInfoInf — {ex.Message}"); break; }
                int rest = total - PacketCodec.HeaderSize - Protocol.GameInfoFraming.PrefixLength;
                if (pos + total > stream.Length) { Console.WriteLine($"  0x{pos:x4}: GameInfoInf truncado"); break; }
                var tail = stream.AsSpan(pos + PacketCodec.HeaderSize + Protocol.GameInfoFraming.PrefixLength, rest).ToArray();
                cipher.Decrypt(tail);
                Console.WriteLine($"  0x{pos:x4}: 0x007a = {total} (variavel)  GameInfoInf");
                pos += total;
                continue;
            }

            int len;
            if (sizes.TryGetValue(id, out int known)) len = known;
            else
            {
                len = FindBoundary(stream, pos, cipher);
                if (len <= 0) { Console.WriteLine($"  0x{pos:x4}: 0x{id:x4} fronteira indeterminada — paragem"); break; }
                sizes[id] = len;
                Console.WriteLine($"  0x{pos:x4}: 0x{id:x4} = {len}  <- descoberto  " +
                                  SizeHarvester.MessageNames.GetValueOrDefault(id, ""));
            }

            if (pos + len > stream.Length)
            { Console.WriteLine($"  0x{pos:x4}: 0x{id:x4} len={len} excede o stream — paragem"); break; }

            if (len >= PacketCodec.MinEncryptedSize)
            {
                var body = stream.AsSpan(pos + 7, len - 7).ToArray();
                cipher.Decrypt(body);      // avanca o estado real
                report.Add($"0x{id:x4} {SizeHarvester.MessageNames.GetValueOrDefault(id, ""),-22} " +
                           $"len={len,4}  {Ascii(body, 96)}");
                decrypted.AddRange(body);
            }
            pos += len;
        }
        Console.WriteLine($"  terminou em 0x{pos:x4} de 0x{stream.Length:x4}" +
                          (pos == stream.Length ? "  (stream consumido por completo)" : ""));
    }

    /// <summary>
    /// Comprimento do pacote em <paramref name="pos"/>, ou -1 se indeterminado.
    ///
    /// Num intervalo de alguns kB so' existem pouquissimos offsets onde os dois bytes
    /// formam um message id valido (116 ids em 65536 => cerca de 7 por 4 kB). Sao tao
    /// poucos que se testam todos: para cada candidato, decifra-se o corpo ate' la' e
    /// depois o inicio do pacote seguinte. So' na fronteira certa e' que a cifra fica
    /// sincronizada e o pacote seguinte decifra com enchimento normal.
    /// </summary>
    private static int FindBoundary(byte[] stream, int pos, DjMaxCipher state)
    {
        int limit = Math.Min(pos + MaxPacket, stream.Length);
        var candidates = new List<int>();
        for (int c = pos + 8; c <= limit - 2; c++)
        {
            // Aceitar qualquer valor no intervalo plausivel de message id, e nao so' os
            // 116 do dispatcher: o extractor do switch perde o primeiro id dos grupos
            // 'case A: case B:', e o servidor pode enviar ids tratados noutro sitio.
            // A discriminacao real vem do enchimento (98% contra 0%), nao da lista.
            if (BitConverter.ToUInt16(stream, c) is > 0 and < 0x400) candidates.Add(c);
        }
        if (limit == stream.Length) candidates.Add(stream.Length);   // pacote final
        if (candidates.Count == 0) return -1;

        int best = -1; double bestScore = -1;
        foreach (var c in candidates)
        {
            int bodyLen = c - pos - 7;
            if (bodyLen < 1) continue;

            var t = state.Clone();
            var body = stream.AsSpan(pos + 7, bodyLen).ToArray();
            t.Decrypt(body);

            double score;
            if (c == stream.Length)
            {
                // fim do stream: so' da' para avaliar a cauda do proprio corpo
                score = Ratio(body, Math.Max(0, body.Length - 48), Math.Min(48, body.Length));
            }
            else
            {
                int nextLen = Math.Min(64, stream.Length - c - 7);
                if (nextLen <= 0) continue;
                var next = stream.AsSpan(c + 7, nextLen).ToArray();
                t.Decrypt(next);
                // o peso esta' no pacote SEGUINTE: e' esse que denuncia a sincronia
                score = 2.0 * Ratio(next, 0, next.Length)
                      + Ratio(body, Math.Max(0, body.Length - 32), Math.Min(32, body.Length));
            }
            if (score > bestScore) { bestScore = score; best = c; }
        }
        if (Diagnose)
        {
            Console.WriteLine($"    [diag] pos=0x{pos:x4} id=0x{BitConverter.ToUInt16(stream, pos):x4} " +
                              $"{candidates.Count} candidatos, melhor=0x{best:x4} score={bestScore:F3}");
            foreach (var c in candidates.Take(12))
            {
                int bodyLen = c - pos - 7;
                if (bodyLen < 1) continue;
                var t = state.Clone();
                var body = stream.AsSpan(pos + 7, bodyLen).ToArray();
                t.Decrypt(body);
                int nextLen = Math.Min(64, stream.Length - c - 7);
                double s = 0;
                if (nextLen > 0) { var nx = stream.AsSpan(c + 7, nextLen).ToArray(); t.Decrypt(nx); s = Ratio(nx, 0, nx.Length); }
                Console.WriteLine($"      len={c - pos,5} -> id seguinte 0x{BitConverter.ToUInt16(stream, c):x4} " +
                                  $"enchimento={s:P0}");
            }
        }
        // exigir evidencia minima para nao inventar fronteiras
        return bestScore >= 0.15 && best > 0 ? best - pos : -1;
    }

    private static double Ratio(byte[] d, int off, int len)
    {
        if (off < 0 || len <= 0 || off + len > d.Length) return 0;
        int f = 0;
        for (int i = off; i < off + len; i++) if (d[i] is 0x00 or 0xCC) f++;
        return (double)f / len;
    }
}
