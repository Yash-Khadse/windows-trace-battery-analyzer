import pytest


@pytest.fixture(autouse=True)
def _allow_test_salt(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setenv("WTBA_ALLOW_TEST_SALT", "1")
