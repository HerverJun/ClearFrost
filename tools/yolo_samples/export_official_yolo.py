"""Export official Ultralytics YOLO nano models to ONNX samples.

The generated .pt and .onnx files are intentionally ignored by the repository
root .gitignore. Run from the repository root:

    python tools/yolo_samples/export_official_yolo.py
"""

from __future__ import annotations

import argparse
import json
from pathlib import Path

from ultralytics import YOLO


SAMPLES = [
    {"task": "detect", "model": "yolo11n.pt", "imgsz": 320},
    {"task": "segment", "model": "yolo11n-seg.pt", "imgsz": 320},
    {"task": "pose", "model": "yolo11n-pose.pt", "imgsz": 320},
    {"task": "obb", "model": "yolo11n-obb.pt", "imgsz": 320},
    {"task": "classify", "model": "yolo11n-cls.pt", "imgsz": 224},
]


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser()
    parser.add_argument(
        "--output",
        default="samples/yolo-official",
        help="Directory for official .pt/.onnx samples.",
    )
    parser.add_argument("--opset", type=int, default=17)
    parser.add_argument("--dynamic", action="store_true")
    parser.add_argument("--nms", action="store_true")
    parser.add_argument("--half", action="store_true")
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    output_dir = Path(args.output)
    output_dir.mkdir(parents=True, exist_ok=True)

    exported = []
    for sample in SAMPLES:
        task_dir = output_dir / sample["task"]
        task_dir.mkdir(parents=True, exist_ok=True)
        model_name = sample["model"]
        model_path = task_dir / model_name

        print(f"[export] {model_name} -> {task_dir}")
        model = YOLO(str(model_path))
        exported_path = model.export(
            format="onnx",
            imgsz=sample["imgsz"],
            opset=args.opset,
            dynamic=args.dynamic,
            nms=args.nms,
            half=args.half,
            simplify=False,
            verbose=False,
        )

        exported.append(
            {
                "task": sample["task"],
                "source": model_name,
                "imgsz": sample["imgsz"],
                "onnx": str(Path(exported_path).as_posix()),
            }
        )

    manifest_path = output_dir / "manifest.json"
    manifest_path.write_text(
        json.dumps(
            {
                "source": "Ultralytics official YOLO11 nano models",
                "format": "onnx",
                "opset": args.opset,
                "dynamic": args.dynamic,
                "nms": args.nms,
                "half": args.half,
                "models": exported,
            },
            ensure_ascii=False,
            indent=2,
        ),
        encoding="utf-8",
    )
    print(f"[manifest] {manifest_path}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
