#!/usr/bin/env python3
"Persistent JSON-lines bridge to RUAccent; model loading happens only once."""
import argparse
import json
import sys
import traceback

def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--model", default="turbo3.1")
    parser.add_argument("--device", choices=("CPU", "CUDA"), default="CPU")
    dictionary_group = parser.add_mutually_exclusive_group()
    dictionary_group.add_argument("--dictionary", dest="dictionary", action="store_true")
    dictionary_group.add_argument("--no-dictionary", dest="dictionary", action="store_false")
    parser.set_defaults(dictionary=True)
    args = parser.parse_args()

    try:
        from ruaccent import RUAccent
        accentizer = RUAccent()
        accentizer.load(
            omograph_model_size=args.model,
            use_dictionary=args.dictionary,
            device=args.device,
        )
        print("READY", flush=True)
    except Exception as exc:
        print("ERROR: " + str(exc), flush=True)
        traceback.print_exc(file=sys.stderr)
        return 1
    for line in sys.stdin:
        try:
            text = json.loads(line)
            if not isinstance(text, str):
                raise ValueError("Ожидалась JSON-строка.")
            result = accentizer.process_all(text)
            print(json.dumps({"text": result}, ensure_ascii=False), flush=True)
        except Exception as exc:
            print(json.dumps({"error": str(exc)}, ensure_ascii=False), flush=True)

    return 0
if __name__ == "__main__":
    raise SystemExit(main())
