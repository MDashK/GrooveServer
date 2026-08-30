using System.Diagnostics;
using System.Runtime.InteropServices;

namespace GrooveServer.Tools;

/// <summary>
/// Despeja a imagem do cliente a partir da MEMORIA do processo vivo.
///
/// O <c>DJMax.exe</c> esta' empacotado com ASProtect — as seccoes `aspr`, `.Silvana` e
/// `.adata` no cabecalho denunciam-no, e o directorio de imports esta' a zero. Desempacotar
/// o ficheiro nao e' preciso: o proprio protector descomprime tudo em memoria antes de
/// correr, por isso ler o processo da' o codigo ja' em claro.
///
/// Nao precisa de privilegios especiais — e' um processo do mesmo utilizador.
///
/// Alem da imagem, escreve um mapa da IAT: cada entrada aponta para uma funcao ja'
/// resolvida noutro modulo, e cruzando com as tabelas de exportacao desses modulos sai o
/// nome. Sem isto o Ghidra mostra saltos indiretos para enderecos anonimos em vez de
/// `recv`, `send` ou as funcoes de cifra — que e' precisamente o que interessa seguir.
/// </summary>
public static class ProcessDump
{
    private const int PROCESS_VM_READ = 0x0010;
    private const int PROCESS_QUERY_INFORMATION = 0x0400;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(int access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadProcessMemory(IntPtr h, IntPtr addr, byte[] buf, int size, out IntPtr read);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr h);

    [DllImport("kernel32.dll")]
    private static extern int VirtualQueryEx(IntPtr h, IntPtr addr, out MEMORY_BASIC_INFORMATION mbi, int len);

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORY_BASIC_INFORMATION
    {
        public IntPtr BaseAddress, AllocationBase;
        public uint AllocationProtect;
        public IntPtr RegionSize;
        public uint State, Protect, Type;
    }

    private const uint MEM_COMMIT = 0x1000;
    private const uint PAGE_NOACCESS = 0x01;
    private const uint PAGE_GUARD = 0x100;

    public static void Run(string nomeProcesso, string destino)
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
            // A base de um EXE de 32 bits e' 0x400000 — confirmado pelos enderecos ja'
            // conhecidos (o global do IP em 0x00AC88C8, FUN_004319d0).
            var baseImg = new IntPtr(0x400000);
            var cab = new byte[0x1000];
            if (!ReadProcessMemory(h, baseImg, cab, cab.Length, out _))
            { Console.WriteLine("nao li o cabecalho em 0x400000"); return; }

            int pe = BitConverter.ToInt32(cab, 0x3C);
            if (BitConverter.ToUInt32(cab, pe) != 0x00004550)
            { Console.WriteLine("assinatura PE nao bate; a imagem nao esta' em 0x400000"); return; }

            int sizeOfImage = BitConverter.ToInt32(cab, pe + 24 + 56);
            Console.WriteLine($"imagem: 0x{sizeOfImage:X} bytes ({sizeOfImage / 1024 / 1024} MB)");

            // Ler regiao a regiao: ha' buracos por mapear no meio da imagem, e um unico
            // ReadProcessMemory sobre tudo falharia por causa deles.
            var imagem = new byte[sizeOfImage];
            long lidos = 0, saltados = 0;
            for (long off = 0; off < sizeOfImage;)
            {
                var addr = new IntPtr(0x400000 + off);
                if (VirtualQueryEx(h, addr, out var mbi, Marshal.SizeOf<MEMORY_BASIC_INFORMATION>()) == 0) break;

                long tam = Math.Min((long)mbi.RegionSize, sizeOfImage - off);
                bool legivel = mbi.State == MEM_COMMIT
                            && (mbi.Protect & PAGE_NOACCESS) == 0
                            && (mbi.Protect & PAGE_GUARD) == 0;
                if (legivel)
                {
                    var buf = new byte[tam];
                    if (ReadProcessMemory(h, addr, buf, (int)tam, out var n))
                    {
                        Array.Copy(buf, 0, imagem, off, (long)n);
                        lidos += (long)n;
                    }
                    else saltados += tam;
                }
                else saltados += tam;
                off += tam;
            }

            File.WriteAllBytes(destino, imagem);
            Console.WriteLine($"escrito {destino}: {lidos / 1024} KB lidos, {saltados / 1024} KB por mapear");
            Console.WriteLine($"carregar no Ghidra como 'Raw Binary', x86 32-bit, base 0x400000");

            EscreverModulos(proc, destino + ".modulos.txt");
        }
        finally { CloseHandle(h); }
    }

    /// <summary>
    /// Procura valores de 32 bits em TODO o espaco de enderecos, nao so' na imagem.
    ///
    /// O catalogo da loja e' carregado do `.pak` para memoria alocada, fora do modulo — por
    /// isso nao aparece no despejo da imagem. Em vez de gravar centenas de MB, varre-se e
    /// mostra-se so' a vizinhanca de cada acerto.
    /// </summary>
    public static void Scan(string nomeProcesso, uint[] valores, int contexto = 32)
    {
        var proc = Process.GetProcessesByName(nomeProcesso).FirstOrDefault();
        if (proc is null) { Console.WriteLine($"processo '{nomeProcesso}' nao encontrado"); return; }
        var h = OpenProcess(PROCESS_VM_READ | PROCESS_QUERY_INFORMATION, false, proc.Id);
        if (h == IntPtr.Zero) { Console.WriteLine("nao consegui abrir o processo"); return; }

        var alvos = valores.Select(v => (Valor: v, Bytes: BitConverter.GetBytes(v))).ToArray();
        var achados = new Dictionary<uint, int>();
        long varrido = 0;

        try
        {
            long addr = 0x10000;
            while (addr < 0x7FFF0000L)
            {
                if (VirtualQueryEx(h, new IntPtr(addr), out var mbi,
                                   Marshal.SizeOf<MEMORY_BASIC_INFORMATION>()) == 0) break;
                long tam = (long)mbi.RegionSize;
                if (tam <= 0) break;

                bool legivel = mbi.State == MEM_COMMIT
                            && (mbi.Protect & PAGE_NOACCESS) == 0
                            && (mbi.Protect & PAGE_GUARD) == 0;
                if (legivel && tam < 64 * 1024 * 1024)
                {
                    var buf = new byte[tam];
                    if (ReadProcessMemory(h, new IntPtr(addr), buf, (int)tam, out var n))
                    {
                        varrido += (long)n;
                        for (int i = 0; i + 4 <= (int)n; i += 4)
                            foreach (var (valor, bytes) in alvos)
                                if (buf[i] == bytes[0] && buf[i + 1] == bytes[1] &&
                                    buf[i + 2] == bytes[2] && buf[i + 3] == bytes[3])
                                {
                                    achados[valor] = achados.GetValueOrDefault(valor) + 1;
                                    if (achados[valor] <= 3)
                                    {
                                        int ini = Math.Max(0, i - contexto);
                                        int fim = Math.Min((int)n, i + contexto);
                                        Console.WriteLine($"  {valor} @ 0x{addr + i:X8}");
                                        Console.WriteLine($"    {Convert.ToHexString(buf, ini, fim - ini)}");
                                    }
                                }
                    }
                }
                addr += tam;
            }
        }
        finally { CloseHandle(h); }

        Console.WriteLine($"\nvarridos {varrido / 1024 / 1024} MB");
        foreach (var (v, n) in achados.OrderBy(x => x.Key)) Console.WriteLine($"  {v}: {n} ocorrencia(s)");
        foreach (var v in valores.Where(v => !achados.ContainsKey(v))) Console.WriteLine($"  {v}: nenhuma");
    }

    /// <summary>
    /// Lista os modulos carregados e onde estao. Serve para dar nome aos saltos indiretos:
    /// uma entrada da IAT que aponte para dentro de um destes intervalos e' uma chamada a
    /// esse modulo, e o offset identifica a funcao na tabela de exportacao.
    /// </summary>
    private static void EscreverModulos(Process proc, string destino)
    {
        var linhas = new List<string>();
        try
        {
            foreach (ProcessModule m in proc.Modules)
                linhas.Add($"0x{m.BaseAddress.ToInt64():X8}\t0x{m.ModuleMemorySize:X8}\t{m.ModuleName}\t{m.FileName}");
        }
        catch (Exception ex) { linhas.Add($"# nao consegui enumerar modulos: {ex.Message}"); }

        File.WriteAllLines(destino, linhas);
        Console.WriteLine($"escrito {destino}: {linhas.Count} modulos");
    }
}
