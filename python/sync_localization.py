import os
import sys
import json
import subprocess
import re

def get_language_score(data_dict, lang):
    """
    Score the language of a localization dictionary.
    """
    sample = " ".join(str(v) for v in list(data_dict.values())[:300]).lower()
    if lang == "zhs":
        count = sum(1 for char in sample if '\u4e00' <= char <= '\u9fff')
        return count
    elif lang == "eng":
        score = 0
        # Common English words
        for w in [" the ", " gain ", " deal ", " draw ", " block ", " card ", " your ", " of ", " to ", " will be ", " future ", " is ", " an ", " on ", " deal ", " damage ", " attack ", " you "]:
            score += sample.count(w) * 10
        
        if "details for this relic will be revealed" in sample: score += 100
        if "details for this card will be revealed" in sample: score += 100

        # Penalize non-English Latin characters (common in Polish, German, French, etc.)
        non_eng = sum(sample.count(char) for char in "ąćęłńóśźżäöüßéèêàâùûîïôœ")
        score -= non_eng * 50
        
        # Specific English Act Name marker
        if "overgrowth" in sample or "underdocks" in sample or "morphic grove" in sample: score += 100
        
        return score
    return 0

def sync_localization():
    """
    Extract localization JSON files from SlayTheSpire2.pck.
    """
    game_dir = os.environ.get("STS2_GAME_DIR")
    if not game_dir:
        import platform
        system = platform.system()
        candidates = ["/media/plp/Games/SteamLibrary/steamapps/common/Slay the Spire 2"]
        if system == "Linux":
            for steam in ["~/.steam/steam", "~/.local/share/Steam"]:
                candidates.append(os.path.expanduser(f"{steam}/steamapps/common/Slay the Spire 2"))
        for d in candidates:
            if os.path.isdir(d):
                game_dir = d
                break
    
    if not game_dir:
        print("❌ Could not find Slay the Spire 2 installation.")
        return False

    pck_path = os.path.join(game_dir, "SlayTheSpire2.pck")
    if not os.path.isfile(pck_path):
        print(f"❌ Could not find SlayTheSpire2.pck at {game_dir}")
        return False

    print(f"🔍 Found game at: {game_dir}")
    print(f"📦 Syncing localization from latest game patch...")

    TABLE_MARKERS = {
        "cards": ["STRIKE_IRONCLAD.title"],
        "relics": ["AKABEKO.title"],
        "potions": ["FIRE_POTION.title"],
        "powers": ["VULNERABLE_POWER.title", "STRENGTH_POWER.title"],
        "events": ["NEOW.title", "MORPHIC_GROVE.title", "POTION_COURIER.title"],
        "monsters": ["BYGONE_EFFIGY.name", "BYRDONIS.name"],
        "encounters": ["TWO_LICE.title", "THREE_SENTRIES.title"],
        "characters": ["IRONCLAD.title"],
        "acts": ["OVERGROWTH.title"],
        "card_keywords": ["RETAIN.title"],
        "afflictions": ["BURN", "BLEED"],
        "enchantments": ["MOMENTUM.title"],
        "intents": ["ATTACK.title"],
        "orbs": ["LIGHTNING.title"],
        "modifiers": ["DRAFT.title"],
    }

    results = {"eng": 0, "zhs": 0}

    # Optimization: Find all offsets in one pass to save time on 1.8GB file
    print(f"  Scanning for all tables in one pass (this may take a minute)...")
    marker_to_table = {}
    all_markers = []
    for table, markers in TABLE_MARKERS.items():
        for m in markers:
            marker_to_table[m] = table
            all_markers.append(m)

    pattern = "|".join(re.escape(m) for m in all_markers)
    all_offsets = {table: [] for table in TABLE_MARKERS}
    
    try:
        # Use -o to only get the matched part, -b for offset, -a for binary
        cmd = ["grep", "-aobE", pattern, pck_path]
        output = subprocess.check_output(cmd, stderr=subprocess.DEVNULL, shell=(os.name == 'nt')).decode()
        for line in output.splitlines():
            if ':' in line:
                offset_str, matched = line.split(':', 1)
                table = marker_to_table.get(matched)
                if table:
                    all_offsets[table].append(int(offset_str))
    except (subprocess.CalledProcessError, FileNotFoundError):
        print("  ⚠ Grep not found or failed, falling back to pure Python search (slower)")
        # Pure Python fallback for finding offsets
        with open(pck_path, 'rb') as f_in:
            # We search in chunks to avoid loading 1.8GB into memory
            chunk_size = 1024 * 1024 * 10 # 10MB
            overlap = 1024 # overlap to catch markers split between chunks
            offset = 0
            while True:
                chunk = f_in.read(chunk_size)
                if not chunk: break
                for m in all_markers:
                    m_bytes = m.encode()
                    p = chunk.find(m_bytes)
                    while p != -1:
                        table = marker_to_table[m]
                        all_offsets[table].append(offset + p)
                        p = chunk.find(m_bytes, p + 1)
                offset += len(chunk)
                f_in.seek(offset - overlap)
                offset -= overlap

    with open(pck_path, 'rb') as f:
        for table, markers in TABLE_MARKERS.items():
            offsets = sorted(list(set(all_offsets.get(table, []))))
            if not offsets:
                print(f"  ⚠ No offsets found for {table} (markers: {', '.join(markers)})")
                continue
            
            print(f"  Extracting {table} ({len(offsets)} candidates)...")
            print(f"    Offsets: {offsets}")
            
            merged_eng = {}
            merged_zhs = {}
            seen_hashes = set()
            
            for offset in offsets:
                # Read a large block around the marker
                f.seek(max(0, offset - 4000000))
                block = f.read(8000000)
                m_pos = offset - max(0, offset - 4000000)
                
                # Verify marker position in block (find any of the markers for this table)
                found_marker = None
                for m in markers:
                    m_bytes = m.encode()
                    if block[m_pos : m_pos + len(m_bytes)] == m_bytes:
                        found_marker = m
                        break
                
                if not found_marker:
                    # Search nearby if exact offset shifted
                    for m in markers:
                        m_bytes = m.encode()
                        pos = block.find(m_bytes, max(0, m_pos - 1000), m_pos + len(m_bytes) + 1000)
                        if pos != -1:
                            found_marker = m
                            m_pos = pos
                            break
                    if not found_marker: continue

                parsed = None
                curr_start = m_pos
                # Find outermost JSON object
                for _ in range(200): # Increased attempts significantly
                    curr_start = block.rfind(b'{', 0, curr_start)
                    if curr_start == -1: break
                    
                    # Sanity check: start of line or after separator
                    # And NOT preceded by a colon (which would indicate a value)
                    if curr_start > 0:
                        prev_char = block[curr_start-1]
                        if prev_char not in [0, 10, 13, 32, 44]:
                            continue
                        if prev_char == 32 and curr_start > 1 and block[curr_start-2] == ord(':'):
                            continue

                    # Faster depth search
                    depth = 0
                    end_pos = -1
                    search_ptr = curr_start
                    while search_ptr < len(block):
                        # Find next brace
                        next_open = block.find(b'{', search_ptr)
                        next_close = block.find(b'}', search_ptr)
                        
                        if next_close == -1: break
                        if next_open != -1 and next_open < next_close:
                            depth += 1
                            search_ptr = next_open + 1
                        else:
                            depth -= 1
                            search_ptr = next_close + 1
                            if depth == 0:
                                if next_close > m_pos:
                                    end_pos = next_close
                                    break
                    
                    if end_pos != -1:
                        try:
                            candidate = block[curr_start : end_pos + 1]
                            import hashlib
                            h = hashlib.md5(candidate).hexdigest()
                            if h in seen_hashes: 
                                parsed = "seen"
                                break
                            
                            data = candidate.decode('utf-8', errors='ignore')
                            temp_parsed = json.loads(data)
                            if found_marker in temp_parsed:
                                parsed = temp_parsed
                                seen_hashes.add(h)
                                break
                        except:
                            pass
                    if parsed: break
                    if curr_start <= 0: break
                
                if parsed and parsed != "seen":
                    eng_score = get_language_score(parsed, "eng")
                    zhs_score = get_language_score(parsed, "zhs")
                    
                    # Be more lenient for small blocks: score > 20 is enough if it's the best match
                    if eng_score > 20 and eng_score > zhs_score:
                        merged_eng.update(parsed)
                        print(f"      + Merged English block (score: {eng_score}, keys: {len(parsed)})")
                    elif zhs_score > 50 and zhs_score > eng_score:
                        merged_zhs.update(parsed)
                        print(f"      + Merged Chinese block (score: {zhs_score}, keys: {len(parsed)})")
            
            if merged_eng:
                os.makedirs("localization_eng", exist_ok=True)
                with open(os.path.join("localization_eng", f"{table}.json"), 'w', encoding='utf-8') as out_f:
                    json.dump(merged_eng, out_f, indent=2, ensure_ascii=False)
                results["eng"] += 1
                print(f"    ✅ eng/{table}.json extracted ({len(merged_eng)} keys)")
            
            if merged_zhs:
                os.makedirs("localization_zhs", exist_ok=True)
                with open(os.path.join("localization_zhs", f"{table}.json"), 'w', encoding='utf-8') as out_f:
                    json.dump(merged_zhs, out_f, indent=2, ensure_ascii=False)
                results["zhs"] += 1
                print(f"    ✅ zhs/{table}.json extracted ({len(merged_zhs)} keys)")

    print(f"✅ Sync complete! Updated {results['eng']} English tables and {results['zhs']} Chinese tables.")
    return True

if __name__ == "__main__":
    sync_localization()
