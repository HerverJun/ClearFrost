"""Create tiny local ONNX fixtures for ClearFrost multitask smoke tests.

The script never downloads or installs packages. If the local Python
environment does not provide `onnx`, it writes ONNX_GENERATION_SKIPPED.txt and
exits successfully so the optional smoke tests can report the skip explicitly.
"""

from __future__ import annotations

from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
OUT_DIR = ROOT / "ClearFrost.Tests" / "TestAssets" / "Models"
SKIPPED = OUT_DIR / "ONNX_GENERATION_SKIPPED.txt"
MAX_MODEL_BYTES = 1_000_000


def write_skip(reason: str) -> None:
    OUT_DIR.mkdir(parents=True, exist_ok=True)
    SKIPPED.write_text(
        "\n".join(
            [
                "ONNX_GENERATION_SKIPPED",
                f"Reason: {reason}",
                "No internet install attempted.",
                "",
            ]
        ),
        encoding="utf-8",
    )
    print(SKIPPED)


try:
    import onnx  # type: ignore
    from onnx import TensorProto, helper  # type: ignore
except Exception:
    write_skip("python package 'onnx' is not installed")
    raise SystemExit(0)


def make_constant_model(name: str, output_specs: list[tuple[str, list[int], list[float]]], metadata: dict[str, str]) -> None:
    input_tensor = helper.make_tensor_value_info("images", TensorProto.FLOAT, [1, 3, 8, 8])
    output_tensors = []
    constant_nodes = []
    for output_name, shape, values in output_specs:
        output_tensors.append(helper.make_tensor_value_info(output_name, TensorProto.FLOAT, shape))
        constant_tensor = helper.make_tensor(f"{output_name}_value", TensorProto.FLOAT, shape, values)
        constant_nodes.append(helper.make_node("Constant", [], [output_name], value=constant_tensor))
    graph = helper.make_graph(constant_nodes, f"{name}_graph", [input_tensor], output_tensors)
    model = helper.make_model(graph, producer_name="ClearFrost smoke")
    model.ir_version = min(model.ir_version, 9)
    for item_key, item_value in metadata.items():
        entry = model.metadata_props.add()
        entry.key = item_key
        entry.value = item_value
    onnx.checker.check_model(model)
    path = OUT_DIR / name
    onnx.save(model, path)
    size = path.stat().st_size
    print(f"{path} {size} bytes")
    if size > MAX_MODEL_BYTES:
        print(f"WARNING: {path.name} is larger than {MAX_MODEL_BYTES} bytes")


def main() -> int:
    OUT_DIR.mkdir(parents=True, exist_ok=True)
    if SKIPPED.exists():
        SKIPPED.unlink()

    make_constant_model(
        "classification_smoke.onnx",
        [("output0", [1, 2], [0.93, 0.07])],
        {"task": "classify", "names": "{0: 'OK', 1: 'NG'}", "version": "8.0.0"},
    )
    make_constant_model(
        "segmentation_smoke.onnx",
        [
            ("output0", [1, 38, 1], [0.0, 0.0, 4.0, 4.0, 0.91, 0.05] + [0.1] * 32),
            ("output1", [1, 32, 4, 4], [0.0] * (32 * 4 * 4)),
        ],
        {"task": "segment", "names": "{0: 'glue', 1: 'void'}", "version": "8.0.0"},
    )
    make_constant_model(
        "obb_smoke.onnx",
        [("output0", [1, 7, 1], [4.0, 4.0, 2.0, 2.0, 0.92, 0.08, 0.25])],
        {"task": "obb", "names": "{0: 'screw', 1: 'body'}", "version": "8.0.0"},
    )
    make_constant_model(
        "pose_smoke.onnx",
        [("output0", [1, 11, 1], [4.0, 4.0, 2.0, 2.0, 0.90, 3.0, 3.0, 0.8, 5.0, 5.0, 0.7])],
        {"task": "pose", "names": "{0: 'person'}", "version": "8.0.0"},
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
