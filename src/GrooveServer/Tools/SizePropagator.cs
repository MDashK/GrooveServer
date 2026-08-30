using System.Globalization;

namespace GrooveServer.Tools;

/// <summary>
/// Deduz tamanhos por msgid com propagacao de restricoes, em vez de busca.
///
/// Ideia: o stream parte-se em TROCOS delimitados por fronteiras obrigatorias
/// (inicios de segmentos TCP que chegaram apos uma pausa, mais o fim do stream).
/// Cada troco tem de decompor-se em pacotes inteiros.
///
/// Percorrendo um troco com os tamanhos ja' conhecidos, chega-se eventualmente a um
/// pacote de tamanho desconhecido. Se esse for o ULTIMO do troco, o seu tamanho fica
/// determinado por subtracao. Repetindo ate' nao haver progresso, a tabela preenche-se
/// sozinha — sem explosao combinatoria e sem ambiguidade.
/// </summary>
public static class SizePropagator
{
    public static void Run(string direction, params string[] paths)
    {
        const double GapSeconds = 0.030;
        var runs = new List<byte[]>();

        foreach (var path in paths)
        {
            if (!File.Exists(path)) continue;
            var segs = File.ReadAllLines(path)
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .Select(l => l.Split('\t'))
                .Where(p => p.Length >= 3 && p[0] == direction && p[2].Length > 0)
                .Select(p => (Time: double.Parse(p[1], CultureInfo.InvariantCulture),
                              Data: Convert.FromHexString(p[2])))
                .ToList();
            if (segs.Count == 0) continue;

            // agrupar segmentos consecutivos sem pausa entre eles = um troco
            var current = new List<byte>();
            for (int i = 0; i < segs.Count; i++)
            {
                if (i > 0 && segs[i].Time - segs[i - 1].Time >= GapSeconds && current.Count > 0)
                { runs.Add(current.ToArray()); current = new List<byte>(); }
                current.AddRange(segs[i].Data);
            }
            if (current.Count > 0) runs.Add(current.ToArray());
        }

        Console.WriteLine($"{direction}: {runs.Count} trocos, {runs.Sum(r => r.Length)} bytes");

        // Tamanhos conhecidos a' partida.
        var sizes = direction == "C2S"
            ? new Dictionary<ushort, int>(ClientToServerSizes)
            : new Dictionary<ushort, int> { [0x03] = 3, [0x07] = 3, [0x0A] = 47 };

        // Deducao por subtracao, mas so' aceite por UNANIMIDADE.
        //
        // Um troco que termine num msgid desconhecido so' determina o seu tamanho se
        // esse for mesmo o ultimo pacote do troco. Como isso nao se sabe a' partida,
        // recolhem-se os candidatos de TODOS os trocos: se o msgid for de facto o
        // ultimo em varios trocos independentes, todos dao o mesmo valor. Havendo
        // discordancia, e' porque em algum deles vinham mais pacotes a seguir — e a
        // deducao e' rejeitada em vez de envenenar a tabela.
        int round = 0;
        var conflicts = new Dictionary<ushort, SortedSet<int>>();
        while (round++ < 50)
        {
            var candidates = new Dictionary<ushort, SortedSet<int>>();
            foreach (var run in runs)
            {
                int off = 0;
                ushort? pendingId = null;
                while (off < run.Length)
                {
                    if (off + 2 > run.Length) { pendingId = null; break; }
                    ushort id = BitConverter.ToUInt16(run, off);
                    if (sizes.TryGetValue(id, out int len))
                    {
                        if (off + len > run.Length) { pendingId = null; break; }
                        off += len;
                    }
                    else { pendingId = id; break; }
                }
                if (pendingId is ushort unknown && off < run.Length)
                {
                    int remaining = run.Length - off;
                    if (remaining is >= 3 and <= 8192)
                    {
                        if (!candidates.TryGetValue(unknown, out var set))
                            candidates[unknown] = set = new SortedSet<int>();
                        set.Add(remaining);
                    }
                }
            }

            var accepted = candidates.Where(kv => kv.Value.Count == 1).ToList();
            foreach (var kv in candidates.Where(kv => kv.Value.Count > 1))
                conflicts[kv.Key] = kv.Value;
            if (accepted.Count == 0) break;
            foreach (var kv in accepted) sizes[kv.Key] = kv.Value.Single();
        }

        if (conflicts.Count > 0)
        {
            Console.WriteLine("\nAmbiguos (varios trocos discordam — nao deduzidos):");
            foreach (var (id, vals) in conflicts.OrderBy(k => k.Key))
                Console.WriteLine($"  0x{id:x4} ({id,3}) candidatos: {string.Join(", ", vals)}" +
                                  $"   {SizeHarvester.MessageNames.GetValueOrDefault(id, "")}");
        }

        Console.WriteLine($"\n=== TAMANHOS DEDUZIDOS ({sizes.Count}) apos {round} rondas ===");
        foreach (var (id, len) in sizes.OrderBy(k => k.Key))
        {
            string name = SizeHarvester.MessageNames.GetValueOrDefault(id, "");
            Console.WriteLine($"  0x{id:x4} ({id,3}) = {len,5}  {name}");
        }

        // Verificacao: quantos trocos partem na perfeicao com esta tabela?
        int full = 0, partial = 0;
        var missing = new SortedSet<ushort>();
        foreach (var run in runs)
        {
            int off = 0; bool ok = true;
            while (off < run.Length)
            {
                if (off + 2 > run.Length) { ok = false; break; }
                ushort id = BitConverter.ToUInt16(run, off);
                if (!sizes.TryGetValue(id, out int len) || off + len > run.Length)
                { ok = false; missing.Add(id); break; }
                off += len;
            }
            if (ok) full++; else partial++;
        }
        Console.WriteLine($"\nTrocos que partem por completo: {full}/{runs.Count}  (incompletos: {partial})");
        if (missing.Count > 0)
            Console.WriteLine("msgid ainda em falta: " + string.Join(", ", missing.Select(m => $"0x{m:x4}")));
    }

    /// <summary>Extraido dos literais do binario (SendSizes2) — ver docs/protocolo-mensagens.md.</summary>
    public static readonly Dictionary<ushort, int> ClientToServerSizes = new()
    {
        [0x0004] = 3,   [0x0006] = 3,   [0x000A] = 23,  [0x000F] = 70,  [0x0011] = 67,
        [0x0017] = 15,  [0x0019] = 13,  [0x001B] = 53,  [0x001D] = 15,  [0x0023] = 15,
        [0x0046] = 25,  [0x004C] = 59,  [0x0053] = 12,  [0x0056] = 12,  [0x0058] = 12,
        [0x005D] = 11,  [0x005F] = 13,  [0x0064] = 11,  [0x0067] = 11,  [0x006A] = 11,
        [0x006C] = 30,  [0x006F] = 59,  [0x0072] = 259, [0x0073] = 11,  [0x0076] = 17,
        [0x009C] = 50,  [0x00A0] = 11,  [0x00A3] = 11,  [0x00A6] = 12,  [0x00B4] = 11,
        [0x00B7] = 11,  [0x00BA] = 12,  [0x00C3] = 35,  [0x00F0] = 144,
    };
}
