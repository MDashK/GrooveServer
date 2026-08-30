using GrooveServer.Net;

namespace GrooveServer.Tools;

/// <summary>
/// Confere que os blocos guardados sao byte a byte iguais aos que o servidor original
/// enviou, re-extraindo-os da captura e comparando com os ficheiros da biblioteca.
///
/// Serve para separar duas causas possiveis quando uma musica nao arranca: ou o bloco
/// esta' corrompido na colheita, ou esta' bom e o problema e' outro. Sem esta separacao
/// so' resta adivinhar.
/// </summary>
public static class LibraryVerify
{
    public static void Run(string capturePath, string directory)
    {
        var lib = new SongLibrary(directory);
        Console.WriteLine($"biblioteca: {lib.Count} charts\n");

        var map = ResponseMap.Load(capturePath);
        var choices = ClientChoices.AtEachStart(capturePath);

        int ok = 0, bad = 0;
        for (int occ = 0; occ < map.OccurrencesOf(Protocol.RequestId.StartReq); occ++)
        {
            var bucket = map.For(Protocol.RequestId.StartReq, occ);
            var info = bucket.FirstOrDefault(m => m.Id == Protocol.GameInfoFraming.MessageId);
            if (info.Body is null || info.Body.Length < 132) continue;

            uint song = Protocol.GameInfoFraming.ReadSongId(info.Body);
            if (occ >= choices.Count) { Console.WriteLine($"arranque #{occ}: sem escolha correspondente"); bad++; continue; }
            var key = new Protocol.SongKey(song, choices[occ].Difficulty);
            var stored = lib.Get(key)?.FirstOrDefault(e => e.Id == Protocol.GameInfoFraming.MessageId).Body;

            Console.WriteLine($"arranque #{occ} â€” {key}");
            Console.WriteLine($"  captura:    {info.Body.Length} bytes");
            if (stored is null) { Console.WriteLine("  biblioteca: AUSENTE"); bad++; continue; }
            Console.WriteLine($"  biblioteca: {stored.Length} bytes");

            // o bloco declara o proprio comprimento no offset 128; tem de bater certo
            int declared = BitConverter.ToInt32(info.Body, Protocol.GameInfoFraming.LengthOffset);
            int expected = info.Body.Length - Protocol.GameInfoFraming.PrefixLength;
            Console.WriteLine(declared == expected
                ? $"  comprimento interno coerente ({declared})"
                : $"  INCOERENTE: declara {declared}, tem {expected}");

            if (stored.Length != info.Body.Length) { Console.WriteLine("  FALHA: tamanhos diferentes"); bad++; continue; }
            int diff = -1;
            for (int i = 0; i < stored.Length; i++) if (stored[i] != info.Body[i]) { diff = i; break; }
            if (diff >= 0)
            {
                // Quantos bytes diferem ao todo, e quais sao — saber SE difere nao chega
                // para perceber se e' um campo mal preenchido ou o bloco todo trocado.
                int total = 0;
                for (int i = 0; i < stored.Length; i++) if (stored[i] != info.Body[i]) total++;
                int fim = Math.Min(stored.Length, 16);
                Console.WriteLine($"  FALHA: difere no offset {diff} ({total} de {stored.Length} bytes)");
                Console.WriteLine($"    captura   : {Convert.ToHexString(info.Body.AsSpan(0, fim))}");
                Console.WriteLine($"    biblioteca: {Convert.ToHexString(stored.AsSpan(0, fim))}");
                bad++;
            }
            else { Console.WriteLine("  OK: identico"); ok++; }
            Console.WriteLine();
        }
        Console.WriteLine($"\n{ok} blocos corretos, {bad} com problema");
    }
}

