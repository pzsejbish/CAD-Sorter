"""Map the calc payload + optimizer result onto title-block attribute tags.

Three provenances, and the distinction matters for review:
  PAYLOAD  — already sent by the portal today; no new form fields needed.
  RESULT   — read from psf_breakdown on the optimizer_result features.
  GAP      — not currently available. Rendered blank rather than guessed at.

Blank-not-guessed is deliberate: these sheets may be sealed, and a fabricated
value in a title block reads as authoritative and survives review.
"""

from typing import Any, Optional

# Tags with no current source. Kept explicit so the gap list is reviewable
# rather than an accident of missing dict keys.
GAPS = {
    "PROJECT_REF_NO":       "IronRidge's own ref no. (e.g. 12188) — not a PZSE field",
    "SYSTEM_SIZE_KWDC":     "needs module count x module wattage; wattage not in payload",
    "SEISMIC_DESIGN_CAT":   "SDC derivable from SdS + risk category; not currently sent",
    "ROOFING_MATERIAL":     "not captured on the intake form",
    "MODULE_MANUFACTURER":  "payload 'manufacturer' is the BALLAST product, not the module",
    "MODULE_MODEL":         "payload 'model' is the BALLAST product, not the module",
    "MODULE_THICKNESS_IN":  "not captured",
    "MODULE_WATTAGE_W":     "not captured",
    "ARRAY_AZIMUTH_DEG":    "per-array in geojson; no single project value",
    "TILT_ANGLE_DEG":       "on the product_ballast row (backend), like tray_weight",
    "ROW_SPACING_IN":       "derivable from panel pitch; not yet computed",
    "INTERROW_SPACING_IN":  "derivable; not yet computed",
    "CMU_DIMS_IN":          "nominal block dimensions not captured (only block_weight)",
}


# IronRidge spells the state out ("ILLINOIS"), while the payload carries a
# two-letter project_state_code. Expand it. Covers all 50 states, DC, the
# territories and the Canadian provinces — PZSE is licensed across the whole
# footprint, so a US-only map would silently pass codes through unexpanded.
_STATE_NAMES = {
    "AL": "Alabama", "AK": "Alaska", "AZ": "Arizona", "AR": "Arkansas",
    "CA": "California", "CO": "Colorado", "CT": "Connecticut",
    "DE": "Delaware", "FL": "Florida", "GA": "Georgia", "HI": "Hawaii",
    "ID": "Idaho", "IL": "Illinois", "IN": "Indiana", "IA": "Iowa",
    "KS": "Kansas", "KY": "Kentucky", "LA": "Louisiana", "ME": "Maine",
    "MD": "Maryland", "MA": "Massachusetts", "MI": "Michigan",
    "MN": "Minnesota", "MS": "Mississippi", "MO": "Missouri",
    "MT": "Montana", "NE": "Nebraska", "NV": "Nevada",
    "NH": "New Hampshire", "NJ": "New Jersey", "NM": "New Mexico",
    "NY": "New York", "NC": "North Carolina", "ND": "North Dakota",
    "OH": "Ohio", "OK": "Oklahoma", "OR": "Oregon", "PA": "Pennsylvania",
    "RI": "Rhode Island", "SC": "South Carolina", "SD": "South Dakota",
    "TN": "Tennessee", "TX": "Texas", "UT": "Utah", "VT": "Vermont",
    "VA": "Virginia", "WA": "Washington", "WV": "West Virginia",
    "WI": "Wisconsin", "WY": "Wyoming",
    # District + territories
    "DC": "District of Columbia", "PR": "Puerto Rico", "GU": "Guam",
    "VI": "U.S. Virgin Islands", "AS": "American Samoa",
    "MP": "Northern Mariana Islands",
    # Canadian provinces and territories
    "AB": "Alberta", "BC": "British Columbia", "MB": "Manitoba",
    "NB": "New Brunswick", "NL": "Newfoundland and Labrador",
    "NS": "Nova Scotia", "NT": "Northwest Territories", "NU": "Nunavut",
    "ON": "Ontario", "PE": "Prince Edward Island", "QC": "Quebec",
    "SK": "Saskatchewan", "YT": "Yukon",
}


def _state_name(code: str) -> str:
    """Two-letter code -> full name. Anything else passes through unchanged,
    so a value already spelled out is left alone rather than mangled."""
    s = (code or "").strip()
    if len(s) == 2:
        return _STATE_NAMES.get(s.upper(), s)
    return s


def _num(v: Any, digits: int = 0) -> str:
    """Format a number, or blank if absent. Never invents a zero."""
    if v is None or v == "":
        return ""
    try:
        f = float(v)
    except (TypeError, ValueError):
        return str(v)
    return f"{f:,.{digits}f}" if digits else f"{f:,.0f}"


def _split_city_state_zip(line2: str) -> tuple[str, str, str]:
    """'McHenry, IL, 60050' -> ('McHenry', 'IL', '60050'). Blank on shortfall."""
    parts = [p.strip() for p in (line2 or "").split(",") if p.strip()]
    while len(parts) < 3:
        parts.append("")
    return parts[0], parts[1], parts[2]


def _feature_props(geojson: dict, ftype: str) -> list:
    return [
        f.get("properties") or {}
        for f in (geojson or {}).get("features", [])
        if ((f.get("properties") or {}).get("type") == ftype)
    ]


def title_block_values(payload: Optional[dict],
                       result_geojson: Optional[dict] = None) -> dict:
    """Build {tag: display string} for the title panel."""
    r = ((payload or {}).get("result") or {})
    city, state, zip_ = _split_city_state_zip(r.get("project_address_line2", ""))

    vals: dict[str, str] = {
        # ── PROJECT ──
        "PROJECT_OWNER":       r.get("project_name") or "",
        "INSTALLER":           r.get("client_company_name") or "",
        "CODE_SET":            r.get("asce_version") or "",

        # ── SITE ──
        "SITE_ADDRESS":        r.get("project_address_line1") or "",
        "CITY":                city,
        "STATE":               _state_name(r.get("project_state_code") or state),
        "ZIP":                 zip_,
        "SNOW_LOAD_PSF":       _num(r.get("ground_snow_load")),
        "WIND_SPEED_MPH":      _num(r.get("wind_speed")),
        "WIND_EXPOSURE_CAT":   r.get("exposure_category") or "",
        "SEISMIC_SDS":         _num(r.get("design_spectral_acceleration_sds"), 3),

        # ── BUILDING ──
        "RISK_CATEGORY":       r.get("risk_category") or "",
        "DECK_HEIGHT_FT":      _num(r.get("building_height")),
        "PARAPET_HEIGHT_IN":   _num(r.get("parapet_height")),
        "ROOF_SLOPE_DEG":      _num(r.get("highest_roof_pitch")),

        # ── MODULE ──
        "MODULE_LENGTH_IN":    _num(r.get("panel_length"), 2),
        "MODULE_WIDTH_IN":     _num(r.get("panel_width"), 2),
        "MODULE_WEIGHT_LB":    _num(r.get("pv_module_weight"), 2),

        # ── SYSTEM ──
        "CMU_WEIGHT_LB":       _num(r.get("block_weight"), 1),
    }

    # ── TOTALS / weights, from psf_breakdown on the optimizer_result features ──
    #
    # The backend already computes these in UnifiedDataManager.calculate_system_psf
    # and attaches them per optimizer_result. Summing the authoritative breakdown
    # beats re-deriving from counts here — notably EQUIPMENT WEIGHT is
    # modules + racking (module_weight + tray_weight), not modules alone.
    if result_geojson:
        opts = _feature_props(result_geojson, "optimizer_result")
        bd = [o.get("psf_breakdown") or {} for o in opts]

        def s(key: str) -> float:
            return sum(float(b.get(key) or 0) for b in bd)

        modules = int(s("num_panels"))
        # Supp bays share the tray hardware spec and are counted in the tray
        # weight total, so they count as chassis here too.
        chassis = int(s("num_trays") + s("num_supp_bays"))
        blocks = int(s("num_blocks"))
        anchors = int(s("num_anchors"))

        equip_wt = s("module_weight") + s("tray_weight")
        ballast_wt = s("block_weight")
        anchor_wt = s("anchor_weight")
        total_wt = equip_wt + ballast_wt + anchor_wt

        # IronRidge prints DEAD LOAD as "9.8 (MAX)" — the governing array, not a
        # project mean.
        psfs = [float(o.get("psf") or 0) for o in opts if o.get("psf")]

        vals.update({
            "MODULE_COUNT":        _num(modules) if modules else "",
            "CHASSIS_COUNT":       _num(chassis) if chassis else "",
            "BALLAST_COUNT":       _num(blocks) if blocks else "",
            "ANCHOR_COUNT":        _num(anchors),
            "EQUIPMENT_WEIGHT_LB": _num(equip_wt) if equip_wt else "",
            "BALLAST_WEIGHT_LB":   _num(ballast_wt) if ballast_wt else "",
            "TOTAL_WEIGHT_LB":     _num(total_wt) if total_wt else "",
            "DEAD_LOAD_PSF":       f"{max(psfs):.1f} (MAX)" if psfs else "",
        })

    # Gaps stay blank, explicitly.
    for tag in GAPS:
        vals.setdefault(tag, "")
    return vals
