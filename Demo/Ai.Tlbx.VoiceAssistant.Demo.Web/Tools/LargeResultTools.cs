using System;
using System.Threading.Tasks;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ai.Tlbx.VoiceAssistant.Interfaces;

namespace Ai.Tlbx.VoiceAssistant.Demo.Web.Tools;

/// <summary>Synthetic, repeatable data. No external operation or customer data is involved.</summary>
public static class LargeResultFixture
{
    public const string CatalogTailCode = "KUPFER-7319";
    public const string DossierTailCode = "ZEDER-8426";
    public static string Create(bool dossier)
    {
        var records = new JsonArray();
        for (var i = 1; i <= (dossier ? 240 : 120); i++)
            records.Add(new JsonObject
            {
                ["id"] = $"POSITION-{i:D4}", ["quantity"] = i % 17 + 1,
                ["unit_price_cents"] = 1250 + i * 37, ["status"] = i % 7 == 0 ? "Prüfung erforderlich" : "freigegeben",
                ["description"] = $"Bauteil {i}: Wärmedämmung und Abdichtung, Ausführung gemäß Zeichnung Z-{i:D4}. Maße vor Ort prüfen; Übergänge, Anschlüsse und mögliche Feuchtigkeit dokumentieren.",
                ["requirements"] = new JsonArray("Chargennummer nachweisen", "Prüfprotokoll vor Abnahme", "Keine Freigabe bei Maßabweichung"),
                ["inspection"] = new JsonObject { ["temperature_c"] = 18 + i % 8, ["tolerance_mm"] = i % 3 + 1,
                    ["notes"] = $"Prüfabschnitt {i}: äußere Oberfläche unbeschädigt; Dichtung vollständig. Rückfrage zu Anschlussdetail bei Änderung der Zeichnung erforderlich." }
            });
        var document = new JsonObject
        {
            ["synthetic"] = true, ["document"] = dossier ? "Projektakte" : "Bauteilkatalog",
            ["opening_reference"] = "ANFANG-2107", ["records"] = records,
            // Facts intentionally after the large records: a prefix-only shortcut cannot answer the test.
            ["final_inspection"] = new JsonObject { ["approval_code"] = dossier ? DossierTailCode : CatalogTailCode,
                ["approved_quantity"] = dossier ? 863 : 417, ["remaining_defects"] = dossier ? 3 : 2,
                ["decision"] = "Freigabe erst nach Beseitigung aller verbleibenden Mängel." }
        };
        return document.ToJsonString(new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    }
}

public sealed class LargeCatalogTool : IVoiceTool
{
    public sealed record Args;
    public string Name => "get_large_catalog";
    public string Description => "Liest einen synthetischen großen Bauteilkatalog mit über 40000 Zeichen. Für Fragen nach Katalog-Freigabecode, freigegebener Menge und Restmängeln den abschließenden Prüfvermerk auswerten.";
    public Type ArgsType => typeof(Args);
    public Task<string> ExecuteAsync(string argumentsJson) => Task.FromResult(LargeResultFixture.Create(false));
}

public sealed class LargeDossierTool : IVoiceTool
{
    public sealed record Args;
    public string Name => "get_large_project_dossier";
    public string Description => "Liest eine synthetische umfangreiche Projektakte mit über 100000 Zeichen. Für Fragen nach Projektakten-Freigabecode, freigegebener Menge und Restmängeln den abschließenden Prüfvermerk auswerten.";
    public Type ArgsType => typeof(Args);
    public Task<string> ExecuteAsync(string argumentsJson) => Task.FromResult(LargeResultFixture.Create(true));
}
