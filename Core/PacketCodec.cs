using System.Text.Json;
using K4os.Compression.LZ4;

namespace NetManCat.Core;

/// <summary>
/// Serializza/decomprime payload con LZ4 per il trasporto TCP.
///
/// Formato wire:
///   [4 byte int32 LE = lunghezza JSON originale][dati LZ4 compressi]
///
/// Questa struttura permette un sanity-check lato server prima di decomprimere.
/// </summary>
public static class PacketCodec
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>
    /// Serializza <paramref name="payload"/> in JSON, comprime con LZ4
    /// e antepone la lunghezza originale in 4 byte.
    /// </summary>
    public static byte[] Encode<T>(T payload)
    {
        byte[] raw  = System.Text.Encoding.UTF8.GetBytes(
                          JsonSerializer.Serialize(payload, JsonOpts));
        byte[] comp = LZ4Pickler.Pickle(raw, LZ4Level.L03_HC);

        // Prefisso 4 byte: dimensione originale (per verifica integrità)
        byte[] packet = new byte[4 + comp.Length];
        BitConverter.GetBytes(raw.Length).CopyTo(packet, 0);
        comp.CopyTo(packet, 4);
        return packet;
    }

    /// <summary>
    /// Decomprime e deserializza un pacchetto ricevuto.
    /// Lancia <see cref="ArgumentException"/> se il payload è invalido.
    /// </summary>
    public static T? Decode<T>(byte[] data)
    {
        if (data.Length < 4)
            throw new ArgumentException("Payload troppo corto per essere un pacchetto valido.");

        byte[] comp = data[4..];
        byte[] raw  = LZ4Pickler.Unpickle(comp);

        return JsonSerializer.Deserialize<T>(raw, JsonOpts);
    }
}
