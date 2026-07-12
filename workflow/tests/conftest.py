"""跨測試檔共用的假物件（原本 test_api.py / test_retrieve.py 各有一份，上移去重）。"""


class FakeBackendResponse:
    """假的 httpx.Response：只提供 retrieve 節點用得到的兩個方法。"""

    def __init__(self, chunks):
        self._chunks = chunks

    def raise_for_status(self) -> None:
        return None

    def json(self) -> dict:
        return {"chunks": self._chunks}
