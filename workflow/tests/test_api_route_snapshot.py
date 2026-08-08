import os

from app.main import app


def test_registered_api_routes_match_reviewed_snapshot() -> None:
    actual = sorted(
        f"{method.upper()} {path}"
        for path, path_item in app.openapi()["paths"].items()
        for method in path_item
        if method.lower() in {"delete", "get", "head", "options", "patch", "post", "put"}
    )
    snapshot_path = __file__.replace(
        "test_api_route_snapshot.py", "route_snapshots/workflow-api.txt"
    )
    if os.environ.get("UPDATE_ROUTE_SNAPSHOTS") == "1":
        with open(snapshot_path, "w", encoding="utf-8", newline="\n") as snapshot:
            snapshot.write("\n".join(actual) + "\n")
        return
    with open(snapshot_path, encoding="utf-8") as snapshot:
        expected = snapshot.read().splitlines()

    assert expected == actual, (
        "Workflow API route snapshot drifted. Review the contract and update "
        f"{snapshot_path} intentionally.\n" + "\n".join(actual)
    )
