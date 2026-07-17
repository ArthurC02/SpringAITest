"""跨測試檔共用的假物件（原本 test_api.py / test_retrieve.py 各有一份，上移去重）。"""


def auth_headers(tenant_id="demo-a", user_id="alice", role="USER") -> dict:
    """組出符合服務間契約的標頭（固定四鍵版）；預設是 demo-a 租戶的一般使用者。

    帶 token=None 可拔標頭的擴充版（test_api.py 等）語意不同，各檔自帶，不併入此處。
    """
    return {
        "X-Internal-Token": "internal-dev-token",
        "X-Tenant-Id": tenant_id,
        "X-User-Id": user_id,
        "X-User-Role": role,
    }


class FakeBackendResponse:
    """假的 httpx.Response：只提供 retrieve 節點用得到的兩個方法。"""

    def __init__(self, chunks):
        self._chunks = chunks

    def raise_for_status(self) -> None:
        return None

    def json(self) -> dict:
        return {"chunks": self._chunks}
