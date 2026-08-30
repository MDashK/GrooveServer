using System.Globalization;
using GrooveServer.Crypto;
using GrooveServer.Protocol;

namespace GrooveServer.Tools;

/// <summary>
/// Decifra uma captura e imprime cada pacote em hex e ASCII, marcando onde aparecem
/// enderecos IP em binario.
///
/// Serve para encontrar enderecos embutidos no payload — o cliente recebe o endereco
/// do servidor de canal dentro de uma mensagem, e nao pelo global que o redirecionador
/// altera. Sem os substituir, o cliente sai do nosso servidor e volta ao original.
/// </summary>
public static class PacketDump
{
    /// <summary>
    /// Extrai as strings de uma mensagem e cruza-as com os ficheiros .pak em disco.
    ///
    /// Serve para responder a' pergunta "porque e' que esta musica nao tem som": a lista
    /// vem do servidor e cobre o servico todo, mas o cliente so' consegue tocar as que
    /// tiver localmente. As que faltam viriam do FTP anunciado em LOADURL.
    /// </summary>
    public static void Songs(string path, ushort msgId, string pakDir)
    {
        var stream = LoadServerStream(path);
        var cipher = MakeCipher(stream);
        int pos = 3 + 47;
        while (pos + 2 <= stream.Length)
        {
            ushort id = BitConverter.ToUInt16(stream, pos);
            int? size = MessageSizes.FromServer(id, stream, pos);
            if (size is null || pos + size.Value > stream.Length) break;
            int len = size.Value;
            byte[] body = Array.Empty<byte>();
            if (len >= PacketCodec.MinEncryptedSize)
            {
                body = stream.AsSpan(pos + 7, len - 7).ToArray();
                cipher.Decrypt(body);
            }
            if (id == msgId)
            {
                Console.WriteLine($"=== estrutura de 0x{id:x4} ({body.Length} bytes) ===");
                for (int i = 0; i < Math.Min(160, body.Length); i += 16)
                {
                    var c = body.AsSpan(i, Math.Min(16, body.Length - i)).ToArray();
                    Console.WriteLine($"  +{i,5}  {string.Join(' ', c.Select(b => b.ToString("x2"))),-47}  " +
                        new string(c.Select(b => b >= 0x20 && b <= 0x7E ? (char)b : '.').ToArray()));
                }
                // Alta entropia sem periodicidade sugere compressao. O cliente traz zlib,
                // por isso vale tentar inflar a partir de varios offsets — o cabecalho
                // limpo do inicio deve ser metadados (tamanho, contagem) antes do bloco.
                Console.WriteLine("\n  tentativa de descompressao:");
                for (int off = 0; off <= 200; off++)
                {
                    foreach (var raw in new[] { true, false })
                    {
                        try
                        {
                            using var ms = new MemoryStream(body, off, body.Length - off);
                            using Stream dec = raw
                                ? new System.IO.Compression.DeflateStream(ms, System.IO.Compression.CompressionMode.Decompress)
                                : new System.IO.Compression.ZLibStream(ms, System.IO.Compression.CompressionMode.Decompress);
                            using var outMs = new MemoryStream();
                            dec.CopyTo(outMs);
                            if (outMs.Length > body.Length)
                            {
                                var data = outMs.ToArray();
                                Console.WriteLine($"    offset {off}, {(raw ? "deflate" : "zlib")}: " +
                                                  $"{data.Length} bytes descomprimidos");
                                var strs = ExtractStrings(data, 4).Take(30).ToList();
                                Console.WriteLine("    primeiras strings: " + string.Join(" | ", strs));
                                off = 999; break;
                            }
                        }
                        catch { }
                    }
                }
                Console.WriteLine();

                var names = ExtractStrings(body, 3).Distinct().ToList();
                Console.WriteLine($"mensagem 0x{id:x4}: {body.Length} bytes, {names.Count} strings\n");

                var paks = Directory.Exists(pakDir)
                    ? Directory.GetFiles(pakDir, "*.pak")
                        .Select(f => Path.GetFileNameWithoutExtension(f).TrimStart('@').ToLowerInvariant())
                        .ToHashSet()
                    : new HashSet<string>();
                Console.WriteLine($"{paks.Count} ficheiros .pak em disco\n");

                int have = 0, missing = 0;
                foreach (var n in names)
                {
                    var key = n.TrimStart('@').ToLowerInvariant();
                    bool present = paks.Contains(key);
                    if (present) have++; else missing++;
                    Console.WriteLine($"  [{(present ? "TEM" : "   ")}] {n}");
                }
                Console.WriteLine($"\ncom pak local: {have}   sem pak local: {missing}");
                return;
            }
            pos += len;
        }
        Console.WriteLine($"mensagem 0x{msgId:x4} nao encontrada na captura");
    }

    private static IEnumerable<string> ExtractStrings(byte[] d, int min)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var b in d)
        {
            if (b >= 0x20 && b <= 0x7E) sb.Append((char)b);
            else { if (sb.Length >= min) yield return sb.ToString(); sb.Clear(); }
        }
        if (sb.Length >= min) yield return sb.ToString();
    }

    private static byte[] LoadServerStream(string path) =>
        File.ReadAllLines(path)
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(l => l.Split('\t'))
            .Where(p => p.Length >= 3 && p[0] == "S2C" && p[2].Length > 0)
            .SelectMany(p => Convert.FromHexString(p[2]))
            .ToArray();

    private static DjMaxCipher MakeCipher(byte[] stream)
    {
        var key = PacketCodec.TransformSessionKey(stream.AsSpan(3 + 7, 32));
        var words = new uint[8];
        for (int w = 0; w < 8; w++) words[w] = BitConverter.ToUInt32(key, w * 4);
        return new DjMaxCipher(BitConverter.ToUInt16(key, 0x1C), 0u, words);
    }

    public static void Run(string path, string highlightIp = "101.32.26.152")
    {
        var stream = File.ReadAllLines(path)
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(l => l.Split('\t'))
            .Where(p => p.Length >= 3 && p[0] == "S2C" && p[2].Length > 0)
            .SelectMany(p => Convert.FromHexString(p[2]))
            .ToArray();

        var needle = System.Net.IPAddress.Parse(highlightIp).GetAddressBytes();
        Console.WriteLine($"{stream.Length} bytes; a procurar {highlightIp} = {Convert.ToHexString(needle)}\n");

        var key = PacketCodec.TransformSessionKey(stream.AsSpan(3 + 7, 32));
        var words = new uint[8];
        for (int w = 0; w < 8; w++) words[w] = BitConverter.ToUInt32(key, w * 4);
        var cipher = new DjMaxCipher(BitConverter.ToUInt16(key, 0x1C), 0u, words);

        Console.WriteLine($"ConnectAck em claro: {Convert.ToHexString(stream.AsSpan(3, 47))}");
        Console.WriteLine($"chave derivada:      {Convert.ToHexString(key)}\n");

        int pos = 3 + 47;
        while (pos + 2 <= stream.Length)
        {
            ushort id = BitConverter.ToUInt16(stream, pos);

            // O GameInfoInf tem o comprimento cifrado dentro do proprio corpo. Sem o tratar,
            // o dump parava no primeiro — e como ele vem no INICIO da rajada de arranque,
            // tudo o que interessa nessa rajada (0x00C4, 0x00CA, 0x0060) ficava invisivel.
            // Passei tempo a comparar duas gravacoes e a obter "0 ocorrencias" das duas,
            // convencido de que as mensagens nao existiam.
            if (id == GameInfoFraming.MessageId)
            {
                int total;
                try { total = GameInfoFraming.ReadTotalLength(stream, pos, cipher, out var prefixo); }
                catch (Exception ex) { Console.WriteLine($"0x{pos:x4}: GameInfoInf ilegivel — {ex.Message}"); break; }
                if (pos + total > stream.Length) { Console.WriteLine($"0x{pos:x4}: GameInfoInf excede o stream"); break; }

                // O prefixo ja' foi decifrado por ReadTotalLength, que avancou a cifra; o
                // resto do bloco tem de passar na mesma pela cifra para o estado ficar certo
                // para as mensagens seguintes.
                int restante = total - PacketCodec.HeaderSize - GameInfoFraming.PrefixLength;
                if (restante > 0)
                {
                    var bloco = stream.AsSpan(pos + PacketCodec.HeaderSize + GameInfoFraming.PrefixLength, restante).ToArray();
                    cipher.Decrypt(bloco);
                }
                Console.WriteLine($"=== 0x{pos:x4}  msgid 0x{id:x4} ({id})  len={total}  GameInfoInf (bloco da musica)");
                pos += total;
                continue;
            }

            int? size = MessageSizes.FromServer(id, stream, pos);
            if (size is null) { Console.WriteLine($"0x{pos:x4}: msgid 0x{id:x4} sem tamanho — paragem"); break; }
            int len = size.Value;
            if (pos + len > stream.Length) { Console.WriteLine($"0x{pos:x4}: excede o stream"); break; }

            var header = stream.AsSpan(pos, Math.Min(7, len)).ToArray();
            byte[] body = Array.Empty<byte>();
            if (len >= PacketCodec.MinEncryptedSize)
            {
                body = stream.AsSpan(pos + 7, len - 7).ToArray();
                cipher.Decrypt(body);
            }

            string name = SizeHarvester.MessageNames.GetValueOrDefault(id, "");
            Console.WriteLine($"=== 0x{pos:x4}  msgid 0x{id:x4} ({id})  len={len}  {name}");
            Console.WriteLine($"    cabecalho: {Convert.ToHexString(header)}");

            var hits = Find(body, needle);
            if (hits.Count > 0)
                Console.WriteLine($"    *** IP encontrado no corpo em: {string.Join(", ", hits.Select(h => $"+{h}"))}");

            for (int i = 0; i < body.Length; i += 16)
            {
                var chunk = body.AsSpan(i, Math.Min(16, body.Length - i)).ToArray();
                string hex = string.Join(' ', chunk.Select(b => b.ToString("x2")));
                string asc = new(chunk.Select(b => b >= 0x20 && b <= 0x7E ? (char)b : '.').ToArray());
                string mark = hits.Any(h => h >= i && h < i + 16) ? "  <<<" : "";
                Console.WriteLine($"    +{i,4}  {hex,-47}  {asc}{mark}");
            }
            Console.WriteLine();
            pos += len;
        }
    }

    private static List<int> Find(byte[] hay, byte[] needle)
    {
        var hits = new List<int>();
        for (int i = 0; i + needle.Length <= hay.Length; i++)
        {
            bool ok = true;
            for (int j = 0; j < needle.Length; j++) if (hay[i + j] != needle[j]) { ok = false; break; }
            if (ok) hits.Add(i);
        }
        return hits;
    }
}
