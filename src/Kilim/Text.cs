using System.Globalization;

namespace Kilim;

/// <summary>Everything the user reads, in Turkish on Turkish Windows and in English elsewhere.</summary>
static class Text
{
    static readonly bool Turkish = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "tr";

    static string T(string english, string turkish) => Turkish ? turkish : english;

    public static string Pattern => T("Pattern", "Desen");
    public static string ShowFolder => T("Show hidden folder", "Saklanan klasörü göster");
    public static string Close => T("Close Kilim", "Kilim'i kapat");
    public static string Open => T("Open", "Aç");
    public static string Restore => T("Move back out", "Geri çıkar");
    public static string FolderRedirected => T(
        @"Kilim's folder (%USERPROFILE%\Kilim) points to another location, so Kilim will not use it. Nothing has been moved.",
        @"Kilim'in klasörü (%USERPROFILE%\Kilim) başka bir yere yönlendirilmiş, bu yüzden Kilim onu kullanmayacak. Hiçbir dosyaya dokunulmadı.");

    public static string PatternName(string id) => id switch
    {
        "sade" => T("Plain", "Sade"),
        "usak" => T("Ushak", "Uşak"),
        "kilim" => T("Anatolian kilim", "Anadolu kilimi"),
        "hereke" => T("Hereke silk", "Hereke ipek"),
        _ => id,
    };
}
