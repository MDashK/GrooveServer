using GrooveServer.Net;

namespace GrooveServer.Tools;

/// <summary>
/// Compara o que o servidor respondeu a um mesmo pedido em ocorrencias diferentes.
///
/// Serve para descobrir o que muda entre dois modos de jogo — o normal e o "music
/// video", por exemplo. Se as mensagens forem as mesmas mas com corpos diferentes, a
/// diferenca esta' nos bytes; se forem mensagens diferentes, esta' na composicao.
/// </summary>
public static class ResponseCompare
{
    public static void Run(string capturePath, ushort requestId, int a, int b)
    {
        var map = ResponseMap.Load(capturePath);
        int n = map.OccurrencesOf(requestId);
        Console.WriteLine($"0x{requestId:x4}: {n} ocorrencias; a comparar #{a} com #{b}\n");
        if (a >= n || b >= n) { Console.WriteLine("indice fora do intervalo"); return; }

        var setA = map.For(requestId, a);
        var setB = map.For(requestId, b);

        Console.WriteLine($"  #{a}: {string.Join(" ", setA.Select(m => $"0x{m.Id:x2}({m.Body.Length})"))}");
        Console.WriteLine($"  #{b}: {string.Join(" ", setB.Select(m => $"0x{m.Id:x2}({m.Body.Length})"))}\n");

        foreach (var msgId in setA.Select(m => m.Id).Union(setB.Select(m => m.Id)).OrderBy(x => x))
        {
            var ma = setA.FirstOrDefault(m => m.Id == msgId);
            var mb = setB.FirstOrDefault(m => m.Id == msgId);
            string name = SizeHarvester.MessageNames.GetValueOrDefault(msgId, "");

            if (ma.Body is null) { Console.WriteLine($"0x{msgId:x4} {name}: SO' em #{b}"); continue; }
            if (mb.Body is null) { Console.WriteLine($"0x{msgId:x4} {name}: SO' em #{a}"); continue; }

            // o GameInfoInf e' o chart, diferente por natureza — nao interessa comparar
            if (msgId == Protocol.GameInfoFraming.MessageId)
            {
                Console.WriteLine($"0x{msgId:x4} {name}: {ma.Body.Length} vs {mb.Body.Length} bytes (chart, ignorado)");
                continue;
            }

            var hdrDiff = Diff(ma.Header, mb.Header, skip: 2);   // byte 2 e' a chave por-pacote
            var bodyDiff = Diff(ma.Body, mb.Body);

            if (hdrDiff.Count == 0 && bodyDiff.Count == 0)
            { Console.WriteLine($"0x{msgId:x4} {name}: identico"); continue; }

            Console.WriteLine($"0x{msgId:x4} {name}:");
            if (hdrDiff.Count > 0)
            {
                Console.WriteLine($"    cabecalho difere em {string.Join(", ", hdrDiff)}");
                Console.WriteLine($"      #{a}: {Convert.ToHexString(ma.Header)}");
                Console.WriteLine($"      #{b}: {Convert.ToHexString(mb.Header)}");
            }
            if (bodyDiff.Count > 0)
            {
                Console.WriteLine($"    corpo difere em {string.Join(", ", bodyDiff.Take(24))}" +
                                  (bodyDiff.Count > 24 ? $" (+{bodyDiff.Count - 24})" : ""));
                foreach (var off in bodyDiff.Take(8))
                    Console.WriteLine($"      +{off,-4}: {ma.Body[off]:x2} vs {mb.Body[off]:x2}");
            }
        }
    }

    private static List<int> Diff(byte[] x, byte[] y, int skip = -1)
    {
        var d = new List<int>();
        for (int i = 0; i < Math.Min(x.Length, y.Length); i++)
            if (i != skip && x[i] != y[i]) d.Add(i);
        return d;
    }
}
