using System.ComponentModel;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Text;

namespace PbmEditor.Core;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Notify(name);
        return true;
    }

    protected void Notify([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>Text fields in PBM databases: base64( uint32 length + gzip(utf8) ).</summary>
public static class PbmCodec
{
    public static string Encode(string text)
    {
        if (text.Length == 0) return "";
        var raw = Encoding.UTF8.GetBytes(text);
        using var ms = new MemoryStream();
        ms.Write(BitConverter.GetBytes((uint)raw.Length));
        using (var gz = new GZipStream(ms, CompressionLevel.Optimal, true)) gz.Write(raw);
        return Convert.ToBase64String(ms.ToArray());
    }

    /// <summary>Strict decode: the length prefix must match, so plain text is not misread as encoded.</summary>
    public static bool TryDecode(string value, out string text)
    {
        text = value;
        if (value.Length == 0) return true;
        try
        {
            var bytes = Convert.FromBase64String(value);
            if (bytes.Length < 5) return false;
            var len = BitConverter.ToUInt32(bytes, 0);
            using var gz = new GZipStream(new MemoryStream(bytes, 4, bytes.Length - 4), CompressionMode.Decompress);
            using var outMs = new MemoryStream();
            gz.CopyTo(outMs);
            if (outMs.Length != len) return false;
            text = Encoding.UTF8.GetString(outMs.ToArray());
            return true;
        }
        catch (Exception e) when (e is FormatException or InvalidDataException or ArgumentException)
        {
            return false;
        }
    }

    // Columns that hold encoded text. Table-scoped where the name is ambiguous.
    static readonly HashSet<string> Encoded = new(StringComparer.OrdinalIgnoreCase)
    {
        "nom_equipe", "nom_court_equipe", "president", "abrev_logo", "abrev_nom", "fake_name",
        "name_chinese", "name_short_chinese", "abrev_name_chinese",
        "nom_ligue", "abreviation_championnat", "nom_championnat_original", "nom_championnat",
        "abreviation_logo", "nom_ligue_chinese", "nom_championnat_chinese", "abreviation_championnat_chinese",
        "joueur_nom", "joueur_prenom", "joueur_suffixe", "joueur_middle", "joueur_surnom",
        "DYN_person.nom", "DYN_person.prenom", "DYN_person.nom_chinese", "DYN_person.prenom_chinese",
        "DYN_facilities.affluence", "DYN_facilities.recettes",
    };

    public static bool IsEncodedColumn(string table, string column) =>
        Encoded.Contains(column) || Encoded.Contains(table + "." + column);
}
