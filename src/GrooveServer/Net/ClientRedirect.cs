using System.Diagnostics;
using System.Runtime.InteropServices;

namespace GrooveServer.Net;

/// <summary>
/// Aponta o cliente para este servidor, trocando o endereco em memoria assim que ele
/// arranca.
///
/// PORQUE NAO SE EDITA O FICHEIRO: o endereco esta' dentro do <c>FILES\DJMax.client</c>,
/// comprimido com ASProtect. Em memoria ve-se em 0x0086397C, DENTRO da imagem do
/// executavel — nao vem de nenhum <c>.pak</c>, ao contrario do que se pensou durante algum
/// tempo. Editar aquilo obrigaria a desempacotar e reconstruir um binario com
/// anti-adulteracao.
///
/// (O <c>FILES\DJMax.exe</c> e' uma copia do client feita pelo utilizador para tentar
/// arrancar sem o lancador; nao funciona e nao faz parte do jogo.)
///
/// O cliente copia o endereco para o global em <c>0x00AC88C8</c> (ver FUN_004b7b30) antes
/// de se ligar. Escrever ai' e' inofensivo: nao toca em disco, nao persiste, e nao precisa
/// de privilegios — e' um processo do mesmo utilizador.
///
/// Isto vive DENTRO do servidor de proposito. Antes era um script a' parte que era preciso
/// lembrar de correr; agora o servidor a correr significa "jogo redirecionado" e o servidor
/// parado significa "jogo vai ao servidor real" — que e' precisamente o interruptor
/// necessario para poder continuar a fazer capturas.
/// </summary>
public static class ClientRedirect
{
    private const int PROCESS_VM_READ = 0x0010;
    private const int PROCESS_VM_WRITE = 0x0020;
    private const int PROCESS_VM_OPERATION = 0x0008;
    private const int PROCESS_QUERY_INFORMATION = 0x0400;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(int access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadProcessMemory(IntPtr h, IntPtr addr, byte[] buf, int size, out int read);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteProcessMemory(IntPtr h, IntPtr addr, byte[] buf, int size, out int written);
    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr h);

    /// <summary>Tamanho do campo do endereco; o resto vai a zero (o cliente le' ate' ao NUL).</summary>
    private const int Janela = 64;

    /// <summary>
    /// Fica a vigiar o processo do cliente e troca o endereco assim que ele aparece.
    /// Volta ao inicio quando o jogo fecha, para funcionar tambem se ele for reaberto.
    /// </summary>
    public static async Task VigiarAsync(Action<string> log, CancellationToken ct)
    {
        var antigo = System.Text.Encoding.ASCII.GetBytes(Config.ServidorOriginal);
        var novo = new byte[Janela];
        System.Text.Encoding.ASCII.GetBytes(Config.ServidorLocal).CopyTo(novo, 0);

        // Nao se procura por NOME: conforme se arranque pelo lancador ou pelo executavel
        // diretamente, o processo chama-se `DJMax.client` ou `DJMax`. Identifica-se pelo
        // que ele tem em memoria — so' o cliente tem o endereco do servidor original no
        // global, e isso e' prova suficiente.
        var tratados = new HashSet<int>();
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var vivos = Process.GetProcesses()
                    .Where(p => p.ProcessName.StartsWith(Config.ProcessoCliente,
                                                         StringComparison.OrdinalIgnoreCase))
                    .ToList();

                tratados.RemoveWhere(pid => vivos.All(p => p.Id != pid));

                foreach (var proc in vivos.Where(p => !tratados.Contains(p.Id)))
                    if (await TentarAsync(proc.Id, antigo, novo, log, ct))
                        tratados.Add(proc.Id);
            }
            catch (Exception ex) { log($"redirecionamento: {ex.Message}"); }

            await Task.Delay(500, ct);
        }
    }

    private static async Task<bool> TentarAsync(int pid, byte[] antigo, byte[] novo,
                                                Action<string> log, CancellationToken ct)
    {
        var h = OpenProcess(PROCESS_VM_READ | PROCESS_VM_WRITE | PROCESS_VM_OPERATION |
                            PROCESS_QUERY_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return false;

        try
        {
            var alvo = new IntPtr(Config.EnderecoGlobal);
            var buf = new byte[Janela];

            // O endereco so' aparece la' uns instantes depois do arranque; insiste-se um
            // bocado antes de desistir deste processo. Os candidatos que nao sao o cliente
            // (o lancador, por exemplo) simplesmente nunca batem e sao descartados.
            for (int tentativa = 0; tentativa < 60 && !ct.IsCancellationRequested; tentativa++)
            {
                if (ReadProcessMemory(h, alvo, buf, Janela, out int lidos) && lidos > 0 &&
                    buf.Take(antigo.Length).SequenceEqual(antigo))
                {
                    if (WriteProcessMemory(h, alvo, novo, Janela, out _))
                    {
                        log($"cliente (pid {pid}) redirecionado: " +
                            $"{Config.ServidorOriginal} -> {Config.ServidorLocal}");
                        return true;
                    }
                    log($"AVISO: nao consegui escrever no cliente (pid {pid})");
                    return false;
                }
                await Task.Delay(50, ct);
            }
            return false;
        }
        finally { CloseHandle(h); }
    }
}
