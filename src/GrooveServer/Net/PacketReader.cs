using System.Net;
using System.Net.Sockets;
using GrooveServer.Protocol;

namespace GrooveServer.Net;

/// <summary>
/// Le' pacotes completos de um fluxo TCP, respeitando o enquadramento real.
///
/// O TCP nao preserva fronteiras de mensagem: um segmento pode conter varios pacotes
/// colados ou meio pacote. Como o protocolo nao transporta comprimento, o unico modo
/// correto de delimitar e' ler os 2 bytes de message id (nunca cifrados) e consultar
/// o tamanho fixo desse id.
/// </summary>
public sealed class PacketReader
{
    private readonly NetworkStream _stream;
    private readonly IReadOnlyDictionary<ushort, int> _sizes;
    private byte[] _buffer = new byte[64 * 1024];
    private int _length;

    public PacketReader(NetworkStream stream, IReadOnlyDictionary<ushort, int> sizes)
    {
        _stream = stream;
        _sizes = sizes;
    }

    /// <summary>
    /// Proximo pacote completo, ou null se a ligacao fechou.
    /// Lanca <see cref="ProtocolViolationException"/> se aparecer um id sem tamanho
    /// conhecido — nesse ponto o fluxo deixa de ser interpretavel e continuar seria
    /// entregar lixo silenciosamente.
    /// </summary>
    public async Task<byte[]?> ReadPacketAsync(CancellationToken ct)
    {
        while (true)
        {
            if (_length >= 2)
            {
                ushort id = BitConverter.ToUInt16(_buffer, 0);
                if (!_sizes.TryGetValue(id, out int size))
                    throw new ProtocolViolationException(
                        $"message id 0x{id:x4} sem tamanho conhecido; " +
                        "impossivel delimitar o pacote (ver docs/tabela-tamanhos.md)");

                if (_length >= size)
                {
                    var packet = _buffer.AsSpan(0, size).ToArray();
                    Buffer.BlockCopy(_buffer, size, _buffer, 0, _length - size);
                    _length -= size;
                    return packet;
                }
                if (size > _buffer.Length) Array.Resize(ref _buffer, size * 2);
            }

            int read = await _stream.ReadAsync(_buffer.AsMemory(_length), ct);
            if (read == 0) return null;
            _length += read;
        }
    }
}
