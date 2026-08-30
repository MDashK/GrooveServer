using System.Diagnostics;
using System.Runtime.InteropServices;

namespace GrooveServer.Pak;

/// <summary>
/// Copia a IMAGEM de um processo para ficheiro, tal e qual esta' em memoria.
///
/// PORQUE E' QUE ISTO VIVE AQUI, no Pak/, e nao no Tools/: o `keys` precisa de um despejo e o
/// reXIP so' compila esta pasta. Sem isto a receita publicada era impossivel de seguir com o
/// reXIP sozinho — mandava correr um `procdump` que ele nao tem.
///
/// **NAO E' UM MINIDUMP.** O ProcDump da Sysinternals escreve um contentor "MDMP" com streams,
/// onde os enderecos nao mapeiam linearmente para posicoes do ficheiro. O `keys` le'
/// `endereco - 0x400000` como posicao, portanto precisa da imagem CRUA — a que comeca no
/// cabecalho PE, em "MZ". Dar-lhe um minidump fazia-o ler zeros e escrever chaves erradas.
///
/// E' tambem por isto que a imagem se le' REGIAO A REGIAO: no meio dela ha' buracos por mapear,
/// e um unico ReadProcessMemory sobre tudo falhava por causa deles.
/// </summary>
public static class ProcessImage
{
    private const int PROCESS_VM_READ = 0x0010;
    private const int PROCESS_QUERY_INFORMATION = 0x0400;
    private const uint MEM_COMMIT = 0x1000;
    private const uint PAGE_NOACCESS = 0x01;
    private const uint PAGE_GUARD = 0x100;

    /// <summary>A base de um EXE de 32 bits.</summary>
    public const long BaseImagem = 0x400000;

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

    /// <summary>
    /// Escreve a imagem do processo em <paramref name="destino"/>. Devolve (lidos, por mapear).
    /// </summary>
    public static (long Lidos, long Saltados) Escrever(string nomeProcesso, string destino,
                                                      Action<string>? log = null)
    {
        log ??= _ => { };

        var proc = Process.GetProcessesByName(nomeProcesso).FirstOrDefault()
                ?? Process.GetProcesses().FirstOrDefault(p => p.ProcessName.Contains(
                       nomeProcesso, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"process '{nomeProcesso}' not found — is the game running?");

        log($"process {proc.ProcessName} (pid {proc.Id})");
        var h = OpenProcess(PROCESS_VM_READ | PROCESS_QUERY_INFORMATION, false, proc.Id);
        if (h == IntPtr.Zero)
            throw new InvalidOperationException(
                "could not open the process — try running this from an elevated prompt");

        try
        {
            var cab = new byte[0x1000];
            if (!ReadProcessMemory(h, new IntPtr(BaseImagem), cab, cab.Length, out _))
                throw new InvalidOperationException($"could not read the header at 0x{BaseImagem:X}");

            int pe = BitConverter.ToInt32(cab, 0x3C);
            if (pe < 0 || pe + 4 > cab.Length || BitConverter.ToUInt32(cab, pe) != 0x00004550)
                throw new InvalidOperationException(
                    $"no PE signature at 0x{BaseImagem:X} — the image is not where this expects it");

            int tamanhoImagem = BitConverter.ToInt32(cab, pe + 24 + 56);
            log($"image: 0x{tamanhoImagem:X} bytes ({tamanhoImagem / 1024 / 1024} MB)");

            var imagem = new byte[tamanhoImagem];
            long lidos = 0, saltados = 0;
            for (long off = 0; off < tamanhoImagem;)
            {
                var addr = new IntPtr(BaseImagem + off);
                if (VirtualQueryEx(h, addr, out var mbi, Marshal.SizeOf<MEMORY_BASIC_INFORMATION>()) == 0) break;

                long tam = Math.Min((long)mbi.RegionSize, tamanhoImagem - off);
                if (tam <= 0) break;

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
            return (lidos, saltados);
        }
        finally { CloseHandle(h); }
    }
}
