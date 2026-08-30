using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace GrooveServer.Tools;

/// <summary>
/// Percorre um <c>std::map</c> do cliente vivo e escreve o conteudo real.
///
/// Porque e' preciso: o `procdump` copia so' a imagem do modulo (0x400000 + SizeOfImage). Os
/// nos de um mapa sao alocados no heap — em `0x040A....` no cliente — e portanto NUNCA
/// apareceram em despejo nenhum. Tudo o que se sabia dos icones vinha de inferir a partir dos
/// CSV de entrada; isto le' o que o cliente REALMENTE construiu a partir deles.
///
/// Os dois mapas que interessam, confirmados no despejo:
///   0x0076CAA4  IconSet   (316 entradas no cliente por tocar)
///   0x00771498  ItemStock (310)
/// Layout do objecto: +0x00 comparador, +0x04 cabeca, +0x08 numero de nos.
///
/// Layout do no' (MSVC classico): _Left, _Parent, _Right, valor, _Color, _Isnil. A cabeca nao
/// e' um elemento: o seu _Parent aponta para a raiz, o _Left para o menor e o _Right para o
/// maior. Percorre-se em ordem, por isso as chaves saem ordenadas — o que serve de prova de
/// que o layout esta' certo.
/// </summary>
public static class MapWalk
{
    private const int PROCESS_VM_READ = 0x0010;
    private const int PROCESS_QUERY_INFORMATION = 0x0400;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(int access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadProcessMemory(IntPtr h, IntPtr addr, byte[] buf, int size, out IntPtr read);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr h);

    private const int Esquerda = 0x00, Pai = 0x04, Direita = 0x08, Valor = 0x0C;

    public static void Run(string nomeProcesso, uint vaDoMapa, int bytesPorNo, string? csv)
    {
        var proc = Process.GetProcessesByName(nomeProcesso).FirstOrDefault()
                ?? Process.GetProcesses().FirstOrDefault(p => p.ProcessName.Contains(nomeProcesso,
                       StringComparison.OrdinalIgnoreCase));
        if (proc is null) { Console.WriteLine($"processo '{nomeProcesso}' nao encontrado"); return; }

        Console.WriteLine($"processo {proc.ProcessName} (pid {proc.Id})");
        var h = OpenProcess(PROCESS_VM_READ | PROCESS_QUERY_INFORMATION, false, proc.Id);
        if (h == IntPtr.Zero) { Console.WriteLine("nao consegui abrir o processo"); return; }

        try
        {
            var cab = Ler(h, vaDoMapa, 12);
            if (cab is null) { Console.WriteLine($"nao li 0x{vaDoMapa:X8}"); return; }

            uint cabeca = BitConverter.ToUInt32(cab, 4);
            int total = BitConverter.ToInt32(cab, 8);
            Console.WriteLine($"mapa 0x{vaDoMapa:X8}: cabeca=0x{cabeca:X8}, {total} nos");
            if (cabeca == 0 || total <= 0 || total > 200000)
            { Console.WriteLine("cabeca ou contagem sem sentido — o mapa nao esta' construido?"); return; }

            var raizNo = Ler(h, cabeca, 16);
            if (raizNo is null) { Console.WriteLine("nao li a cabeca"); return; }
            uint raiz = BitConverter.ToUInt32(raizNo, Pai);

            var linhas = new List<string>();
            int n = 0;
            foreach (uint no in EmOrdem(h, raiz, cabeca, total))
            {
                var b = Ler(h, no, bytesPorNo);
                if (b is null) continue;
                n++;

                int chave = BitConverter.ToInt32(b, Valor);
                var corpo = b.AsSpan(Valor + 4);
                string hex = Convert.ToHexString(corpo);

                string txt = Legivel(corpo);
                // Com ficheiro de saida, mostrar so' o principio: 316 nos dao uma parede de
                // texto que esconde o resumo do fim.
                if (csv is null || n <= 20)
                {
                    Console.WriteLine($"[{n,4}] no=0x{no:X8} chave={chave,6} (0x{chave:X4})");
                    Console.WriteLine($"       {hex}");
                    if (txt.Length > 0) Console.WriteLine($"       \"{txt}\"");
                }
                else if (n == 21) Console.WriteLine("       [...] o resto vai so' para o ficheiro");

                linhas.Add($"{chave},0x{no:X8},{hex},{txt}");
            }

            Console.WriteLine($"\npercorridos {n} nos (o mapa diz {total})");
            if (n != total) Console.WriteLine("AVISO: contagem diferente — o layout do no' pode estar errado");

            if (csv is not null)
            {
                File.WriteAllLines(csv, new[] { "chave,no,bytes,texto" }.Concat(linhas));
                Console.WriteLine($"escrito {csv}");
            }
        }
        finally { CloseHandle(h); }
    }

    /// <summary>
    /// Travessia em ordem, iterativa e com tecto: se o layout estiver errado os ponteiros sao
    /// lixo e uma recursao ingenua nunca mais para.
    /// </summary>
    private static IEnumerable<uint> EmOrdem(IntPtr h, uint raiz, uint cabeca, int tecto)
    {
        var pilha = new Stack<uint>();
        uint act = raiz;
        int guarda = 0;

        while ((act != cabeca && act != 0) || pilha.Count > 0)
        {
            if (++guarda > tecto * 4 + 64) yield break;

            while (act != cabeca && act != 0)
            {
                pilha.Push(act);
                var b = Ler(h, act, 16);
                if (b is null) { act = cabeca; break; }
                act = BitConverter.ToUInt32(b, Esquerda);
            }
            if (pilha.Count == 0) yield break;

            uint no = pilha.Pop();
            yield return no;

            var d = Ler(h, no, 16);
            act = d is null ? cabeca : BitConverter.ToUInt32(d, Direita);
        }
    }

    private static byte[]? Ler(IntPtr h, uint va, int n)
    {
        var buf = new byte[n];
        return ReadProcessMemory(h, new IntPtr(va), buf, n, out var lidos) && (int)lidos == n ? buf : null;
    }

    /// <summary>Extrai as cadeias ASCII de 3+ caracteres do corpo, para dar nome ao que se ve'.</summary>
    private static string Legivel(ReadOnlySpan<byte> b)
    {
        var saida = new List<string>();
        var actual = new StringBuilder();
        foreach (byte c in b)
        {
            if (c >= 0x20 && c < 0x7F) actual.Append((char)c);
            else { if (actual.Length >= 3) saida.Add(actual.ToString()); actual.Clear(); }
        }
        if (actual.Length >= 3) saida.Add(actual.ToString());
        return string.Join(" | ", saida);
    }
}
