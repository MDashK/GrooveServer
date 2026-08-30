using GrooveServer.Net;

namespace GrooveServer.Tools;

/// <summary>
/// Compara a mesma mensagem entre DUAS capturas diferentes.
///
/// O <see cref="ResponseCompare"/> compara ocorrencias dentro da mesma sessao; este
/// compara sessoes. Serve para perceber o que muda de sessao para sessao numa mensagem
/// que o servidor reenvia gravada — se ela contiver identificadores da sessao onde foi
/// gravada, reproduzi-la noutra pode deixar o cliente com uma ideia errada do estado.
/// </summary>
public static class CrossCompare
{
    public static void Run(string captureA, string captureB, ushort msgId)
    {
        var a = First(captureA, msgId);
        var b = First(captureB, msgId);
        string name = SizeHarvester.MessageNames.GetValueOrDefault(msgId, "");
        Console.WriteLine($"0x{msgId:x4} {name}\n");

        if (a is null || b is null)
        {
            Console.WriteLine($"  {Path.GetFileName(captureA)}: {(a is null ? "ausente" : "presente")}");
            Console.WriteLine($"  {Path.GetFileName(captureB)}: {(b is null ? "ausente" : "presente")}");
            return;
        }

        Console.WriteLine($"  A = {Path.GetFileName(captureA)}  ({a.Length} bytes)");
        Console.WriteLine($"  B = {Path.GetFileName(captureB)}  ({b.Length} bytes)\n");

        int len = Math.Min(a.Length, b.Length);
        var diff = Enumerable.Range(0, len).Where(i => a[i] != b[i]).ToList();

        for (int i = 0; i < len; i += 16)
        {
            var ca = a.AsSpan(i, Math.Min(16, len - i)).ToArray();
            var cb = b.AsSpan(i, Math.Min(16, len - i)).ToArray();
            bool linhaDifere = Enumerable.Range(i, ca.Length).Any(diff.Contains);
            Console.WriteLine($"  +{i,4} A {string.Join(' ', ca.Select(x => x.ToString("x2"))),-47}  " +
                              new string(ca.Select(x => x >= 0x20 && x <= 0x7E ? (char)x : '.').ToArray()));
            if (linhaDifere)
                Console.WriteLine($"       B {string.Join(' ', cb.Select(x => x.ToString("x2"))),-47}  " +
                                  new string(cb.Select(x => x >= 0x20 && x <= 0x7E ? (char)x : '.').ToArray()) + "   <<<");
        }

        Console.WriteLine(diff.Count == 0
            ? "\n  identicas"
            : $"\n  diferem em {diff.Count} bytes: {string.Join(", ", diff.Take(30))}" +
              (diff.Count > 30 ? " ..." : ""));
    }

    private static byte[]? First(string path, ushort msgId)
    {
        var map = ResponseMap.Load(path, null, null);
        foreach (var reqId in map.KnownRequests)
            for (int i = 0; i < map.OccurrencesOf(reqId); i++)
                foreach (var m in map.For(reqId, i))
                    if (m.Id == msgId) return m.Body;
        foreach (var m in map.Greeting) if (m.Id == msgId) return m.Body;
        return null;
    }
}
