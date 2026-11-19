"""Static AD integration checks; no domain controller required."""
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def test_ldap_skill_configuration():
    skills = json.loads((ROOT / "skills" / "ad_ldap.json").read_text(encoding="utf-8"))
    settings = json.loads((ROOT / "AD-config.example.json").read_text(encoding="utf-8"))["ActiveDirectory"]
    allowed = set(settings["Servers"]["CORP_AD"]["AllowedAttributes"])
    assert set(skills) == {"ad_ldap_get_user", "ad_ldap_search_users", "ad_ldap_get_user_groups", "ad_ldap_get_group_members", "ad_ldap_check_user_status"}
    for item in skills.values():
        assert item["risk"] == "read"
        assert item["ldap_server"] == "CORP_AD"
        assert set(item["ldap_attributes"]).issubset(allowed)
        assert "ldap_filter" in item and item["ldap_filter"].startswith("(")
    assert settings["Servers"]["CORP_AD"]["UseSsl"] is True
    assert settings["Enabled"] is False


def test_executor_has_restricted_operations():
    source = (ROOT / "LdapQueryExecutor.cs").read_text(encoding="utf-8")
    assert "EscapeFilter" in source
    assert "ModifyRequest" not in source
    assert "AddRequest" not in source
    assert "DeleteRequest" not in source
    assert "SecureSocketLayer = true" in source
    assert "VerifyServerCertificate =" not in source


def test_registration_is_wired():
    assert 'AddSingleton<LdapQueryExecutor>()' in (ROOT / "Program.cs").read_text(encoding="utf-8")
    registry = (ROOT / "SkillRegistry.cs").read_text(encoding="utf-8")
    assert '"ldap" => await _ldap.ExecuteAsync' in registry
    assert 'simple.Risk == "write"' in registry
