namespace Tower.Core.Models;

// ponytail: plaintext-at-rest in tower.db. Fine for a single-user LAN box whose
// disk already holds plaintext creds in appsettings/journald. Upgrade path if this
// ever leaves the LAN: encrypt Value with a key outside the DB (DPAPI/age/libsodium).
public class Secret
{
    public int Id { get; set; }
    public string Project { get; set; } = "";   // grouping label, e.g. "DesignWorks"
    public string Label { get; set; } = "";      // what it is, e.g. "PostgreSQL — designworks user"
    public string Value { get; set; } = "";
    public string? Notes { get; set; }
    public DateTime UpdatedAt { get; set; }
}
