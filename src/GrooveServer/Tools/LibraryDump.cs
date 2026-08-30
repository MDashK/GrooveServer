using GrooveServer.Net;

namespace GrooveServer.Tools;

/// <summary>
/// Mostra o conteudo dos grupos de arranque guardados, para ver o que varia de musica
/// para musica.
///
/// O <c>GameInfoInf</c> e' obviamente diferente em cada uma. A questao e' as outras
/// cinco mensagens do grupo: se forem iguais, podem servir-se de qualquer uma; se
/// variarem, e' preciso perceber com o que e' que variam — com a musica, ou com o
/// estado da sessao em que foram gravadas.
/// </summary>
public static class LibraryDump
{
    public static void Run(string directory)
    {
        var lib = new SongLibrary(directory);
        Console.WriteLine($"{lib.Count} charts:");
        foreach (var k in lib.Keys) Console.WriteLine($"  {k}");
        Console.WriteLine();

        var groups = lib.Keys.ToDictionary(k => k, k => lib.Get(k)!);

        // que message ids aparecem em todos os grupos?
        var ids = groups.Values.SelectMany(g => g.Select(e => e.Id)).Distinct().OrderBy(x => x).ToList();

        foreach (var msgId in ids)
        {
            Console.WriteLine($"=== 0x{msgId:x4} {SizeHarvester.MessageNames.GetValueOrDefault(msgId, "")} ===");
            var samples = new List<(Protocol.SongKey Song, byte[] Header, byte[] Body)>();
            foreach (var (song, group) in groups.OrderBy(k => k.Key))
            {
                var e = group.FirstOrDefault(x => x.Id == msgId);
                if (e.Body is null) { Console.WriteLine($"  musica {song}: ausente"); continue; }
                samples.Add((song, e.Header, e.Body));
            }

            if (msgId == Protocol.GameInfoFraming.MessageId)
            {
                // O cabecalho do bloco (128 bytes antes do campo de comprimento) pode
                // conter estado da sessao em que foi gravado, e nao so' dados da musica.
                // Se assim for, a musica gravada em primeiro lugar tera' valores
                // diferentes das seguintes — e e' precisamente essa que funciona.
                // O cabecalho de 7 bytes do PACOTE (nao do bloco). Ao substituir o corpo
                // mantem-se este cabecalho do template; se ele disser algo sobre o corpo,
                // a substituicao produz um pacote incoerente.
                Console.WriteLine("  cabecalho do PACOTE (7 bytes):");
                foreach (var (song, header, body) in samples)
                    Console.WriteLine($"    musica {song}: {Convert.ToHexString(header)}   " +
                                      $"[3..4]={BitConverter.ToUInt16(header, 3),6}  " +
                                      $"[5..6]={BitConverter.ToUInt16(header, 5),6}  " +
                                      $"(corpo {body.Length}, pacote {7 + body.Length})");

                Console.WriteLine("  cabecalho do bloco (primeiros 32 bytes):");
                foreach (var (song, _, body) in samples)
                    Console.WriteLine($"    musica {song}: {Convert.ToHexString(body.AsSpan(0, 32).ToArray())}" +
                                      $"   ({body.Length} bytes)");

                Console.WriteLine("  campos do cabecalho:");
                foreach (var (song, _, body) in samples)
                    Console.WriteLine($"    musica {song}: [0..1]={BitConverter.ToUInt16(body, 0),5}  " +
                                      $"[2..3]={BitConverter.ToUInt16(body, 2),5}  " +
                                      $"[4..7]={BitConverter.ToUInt32(body, 4),5}  " +
                                      $"[128..131]={BitConverter.ToInt32(body, 128),7}");

                int hlen = 128;
                var hdrVar = Enumerable.Range(0, hlen)
                    .Where(i => samples.Any(s => s.Body[i] != samples[0].Body[i])).ToList();
                Console.WriteLine($"  offsets do cabecalho que variam entre musicas: " +
                                  (hdrVar.Count == 0 ? "nenhum" : string.Join(", ", hdrVar)));
                Console.WriteLine();
                continue;
            }

            foreach (var (song, header, body) in samples)
                Console.WriteLine($"  musica {song}: hdr {Convert.ToHexString(header)}  corpo {Convert.ToHexString(body)}");

            // que bytes variam entre musicas?
            if (samples.Count > 1)
            {
                int len = samples.Min(s => s.Body.Length);
                var varying = Enumerable.Range(0, len)
                    .Where(i => samples.Any(s => s.Body[i] != samples[0].Body[i])).ToList();
                Console.WriteLine(varying.Count == 0
                    ? "  -> corpo IGUAL em todas as musicas"
                    : $"  -> variam os offsets: {string.Join(", ", varying)}");

                int hlen = samples.Min(s => s.Header.Length);
                var hvar = Enumerable.Range(0, hlen)
                    .Where(i => samples.Any(s => s.Header[i] != samples[0].Header[i])).ToList();
                // o byte 2 do cabecalho e' a chave por-pacote, varia sempre
                hvar.Remove(2);
                Console.WriteLine(hvar.Count == 0
                    ? "  -> cabecalho igual (tirando a chave por-pacote)"
                    : $"  -> cabecalho varia nos offsets: {string.Join(", ", hvar)}");
            }
            Console.WriteLine();
        }
    }
}
