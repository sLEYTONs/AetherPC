using AetherPC.Core.Localization;
using AetherPC.Infrastructure.Localization;
var loc = new CatalogLocalizer();
Loc.Use(loc);
var v = Loc.Validate();
Console.WriteLine(v.Ok ? $"OK keys ES={loc.T("Nav.Home")} EN check..." : "FAIL");
Console.WriteLine($"MissingEn={v.MissingInEn.Count} MissingEs={v.MissingInEs.Count} Empty={v.EmptyValues.Count}");
if (!v.Ok) {
  Console.WriteLine("Missing EN: " + string.Join(", ", v.MissingInEn.Take(30)));
  Console.WriteLine("Missing ES: " + string.Join(", ", v.MissingInEs.Take(30)));
}
loc.SetLanguage("en");
Console.WriteLine("EN Nav.Home=" + Loc.T("Nav.Home"));
Console.WriteLine("EN Status.Applied=" + Loc.T("Status.Applied"));
Console.WriteLine("EN Exec.Done=" + Loc.T("Exec.Done", 3, 1, 2));
