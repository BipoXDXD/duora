using System.Net;
using System.Text;

namespace Duora.IntegrationTests.Geolocation;

/// <summary>Builds tiny MMDBs from invented documentation networks. No vendor database or participant data.</summary>
internal static class SyntheticMmdb
{
    public const string DatabaseType = "synthetic-ipinfo-country";
    private static readonly string[] Languages = ["en"];
    private static readonly byte[] Marker = [0xab, 0xcd, 0xef];

    public static byte[] Create(DateTimeOffset builtAt, string type = DatabaseType, string country = "BR", bool extraAsn = false)
    {
        var root = new Node();
        var maps = new[]
        {
            new Dictionary<string, object> { ["country"] = country },
            new Dictionary<string, object> { ["country"] = "US" },
        };
        if (extraAsn) { maps[0]["asn"] = "AS0"; }
        Add(root, "192.0.2.0", 24, 0);
        Add(root, "203.0.113.0", 24, 1);
        Add(root, "2001:db8::", 32, 0);
        var nodes = new List<Node>();
        Index(root, nodes);
        var data = new List<byte>();
        var offsets = maps.Select(m => { var at = data.Count; data.AddRange(Encode(m)); return at; }).ToArray();
        var file = new List<byte>();
        foreach (var n in nodes)
        {
            foreach (var child in n.Children)
            {
                var pointer = child is null ? nodes.Count : child.Data is { } value ? nodes.Count + 16 + offsets[value] : child.Index;
                file.Add((byte)(pointer >> 16)); file.Add((byte)(pointer >> 8)); file.Add((byte)pointer);
            }
        }
        file.AddRange(new byte[16]); file.AddRange(data);
        file.AddRange(Marker); file.AddRange(Encoding.ASCII.GetBytes("MaxMind.com"));
        file.AddRange(Encode(new Dictionary<string, object>
        {
            ["binary_format_major_version"] = (ushort)2, ["binary_format_minor_version"] = (ushort)0,
            ["build_epoch"] = (ulong)builtAt.ToUnixTimeSeconds(), ["database_type"] = type,
            ["description"] = new Dictionary<string, object> { ["en"] = "Synthetic test data" },
            ["ip_version"] = (ushort)6, ["languages"] = Languages,
            ["node_count"] = (uint)nodes.Count, ["record_size"] = (ushort)24,
        }));
        return file.ToArray();
    }

    private static void Add(Node root, string text, int prefix, int data)
    {
        var address = IPAddress.Parse(text); var bytes = address.GetAddressBytes();
        if (bytes.Length == 4) { bytes = new byte[12].Concat(bytes).ToArray(); prefix += 96; }
        var n = root;
        for (var bit = 0; bit < prefix; bit++)
        {
            var side = (bytes[bit / 8] >> (7 - bit % 8)) & 1;
            n.Children[side] ??= new Node(); n = n.Children[side]!;
        }
        n.Data = data;
    }

    private static void Index(Node n, List<Node> nodes)
    {
        n.Index = nodes.Count; nodes.Add(n);
        foreach (var child in n.Children)
        {
            if (child is { Data: null }) { Index(child, nodes); }
        }
    }

    private static byte[] Encode(object value)
    {
        if (value is Dictionary<string, object> map)
        {
            return Header(7, map.Count).Concat(map.SelectMany(p => Encode(p.Key).Concat(Encode(p.Value)))).ToArray();
        }
        if (value is string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text); return Header(2, bytes.Length).Concat(bytes).ToArray();
        }
        if (value is string[] array) { return Header(11, array.Length).Concat(array.SelectMany(v => Encode(v))).ToArray(); }
        var type = value is ushort ? 5 : value is uint ? 6 : 9;
        var number = value switch { ushort u => u, uint u => u, ulong u => u, _ => throw new ArgumentException("Synthetic type unsupported.", nameof(value)) };
        var result = new List<byte>();
        for (var n = number; n != 0; n >>= 8) { result.Insert(0, (byte)n); }
        return Header(type, result.Count).Concat(result).ToArray();
    }

    private static IEnumerable<byte> Header(int type, int size)
    {
        var control = (byte)((type < 8 ? type << 5 : 0) | (size < 29 ? size : 29));
        yield return control;
        if (type >= 8) { yield return (byte)(type - 7); }
        if (size >= 29) { yield return checked((byte)(size - 29)); }
    }

    private sealed class Node
    {
        public Node?[] Children { get; } = new Node?[2];
        public int? Data { get; set; }
        public int Index { get; set; }
    }
}
