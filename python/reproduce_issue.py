import json
import os
import sys
import subprocess

def reproduce(log_path):
    actions = []
    character = "Ironclad"
    seed = "test"
    
    # Try to extract character and seed from filename
    # Format: 20260502_143605_Ironclad_cli_3959.jsonl
    basename = os.path.basename(log_path)
    parts = basename.split("_")
    if len(parts) >= 4:
        character = parts[2]
        seed = "_".join(parts[3:]).replace(".jsonl", "")

    with open(log_path, "r", encoding="utf-8", errors="replace") as f:
        for line in f:
            if not line.strip():
                continue
            entry = json.loads(line)
            if entry["type"] == "action":
                cmd = entry["data"]
                if cmd.get("cmd") != "quit":
                    actions.append(cmd)
    
    print(f"Replaying {len(actions)} actions for {character} with seed {seed}")
    
    # Run play.py with these actions
    # We'll create a temporary save file
    temp_save = "reproduce_temp.json"
    with open(temp_save, "w") as f:
        json.dump({
            "character": character,
            "seed": seed,
            "actions": actions
        }, f)
    
    env = os.environ.copy()
    env["PYTHONIOENCODING"] = "utf-8"
    subprocess.run([sys.executable, "python/play.py", "--load", temp_save], check=True, env=env)


if __name__ == "__main__":
    if len(sys.argv) < 2:
        print("Usage: python python/reproduce_issue.py <log_path>")
        sys.exit(1)
    reproduce(sys.argv[1])
