from __future__ import annotations

import hashlib
import json
import logging
import re
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

import httpx

from .synthetic_pdf import REGION_LABELS, render_jaw
from .backboard_pdf import LEADER_ENDPOINTS

log = logging.getLogger("jaw_quiz_proxy.synthetic_backboard")
PROXY_ROOT = Path(__file__).resolve().parents[1]
PROJECT_ROOT = PROXY_ROOT.parents[1]
ARTIFACT_ROOT = PROJECT_ROOT / "Artifacts" / "SyntheticTeacherDemo_5Rounds_2026-07-24"
PDF_ROOT = ARTIFACT_ROOT / "pdf"
RECEIPT_ROOT = ARTIFACT_ROOT / "receipts"
VIEWS = {"front", "left", "right", "superior"}
TYPES = {"diagram_label", "left_right", "matching", "short_answer"}
PRIORITIES = {"high", "medium", "low"}
UNSAFE = re.compile(r"https?://|<\s*/?\s*[a-z]|javascript:|(?:^|[\\/])\\.\\.(?:[\\/]|$)", re.I)


class ProposalError(ValueError):
    pass


def sha256_bytes(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def discover_gpt55(catalogue: Any) -> dict:
    models = catalogue.get("models", []) if isinstance(catalogue, dict) else []
    exact = []
    alternatives = []
    for model in models:
        if not isinstance(model, dict):
            continue
        name = str(model.get("name") or model.get("model_name") or "").strip()
        label = str(model.get("display_name") or model.get("label") or "").strip()
        provider = str(model.get("provider") or "").strip()
        if "gpt" in (name + label).lower() and provider:
            alternatives.append({
                "provider": provider, "model_name": name,
                "supports_json_output": bool(model.get("supports_json_output"))})
        if name.lower() == "gpt-5.5" or label.lower() == "gpt-5.5":
            exact.append(model)
    compatible = [m for m in exact if m.get("supports_json_output") is True
                  and str(m.get("provider") or "").strip()
                  and str(m.get("name") or m.get("model_name") or "").strip()]
    selected = compatible[0] if len(compatible) == 1 else None
    return {
        "available": bool(exact), "compatible": selected is not None,
        "provider": str(selected.get("provider")) if selected else "",
        "model_name": str(selected.get("name") or selected.get("model_name")) if selected else "",
        "supports_json_output": bool(selected and selected.get("supports_json_output")),
        "memory_mode": "Memory Lite Auto (storage), Readonly (generation)",
        "reason": ("confirmed" if selected else
                   "GPT-5.5 is absent" if not exact else
                   "GPT-5.5 lacks confirmed structured JSON support or is ambiguous"),
        "alternatives": alternatives[:12],
    }


def validate_proposal(raw: str | dict, allowlist: set[str], expected_model: str) -> dict:
    if isinstance(raw, str):
        if len(raw) > 50000:
            raise ProposalError("Response exceeds the safe length limit")
        try:
            value = json.loads(raw)
        except json.JSONDecodeError as exc:
            raise ProposalError("Backboard response was not strict JSON") from exc
    else:
        value = raw
    if not isinstance(value, dict):
        raise ProposalError("Proposal root must be an object")
    required = {"synthetic", "student_label", "model", "five_round_summary", "strengths",
                "needs_practice", "confusion_pairs", "progression",
                "assessment_strategy", "questions"}
    if required - value.keys():
        raise ProposalError("Proposal is missing required fields")
    if value["synthetic"] is not True or value["student_label"] != "Synthetic Student 1":
        raise ProposalError("Proposal synthetic/student identity mismatch")
    if value["model"] != expected_model:
        raise ProposalError("Proposal model provenance mismatch")
    if UNSAFE.search(json.dumps(value, ensure_ascii=True)):
        raise ProposalError("External URL, HTML, executable content, or path traversal rejected")
    if not isinstance(value["questions"], list) or not 8 <= len(value["questions"]) <= 10:
        raise ProposalError("Proposal must contain 8–10 questions")
    ids: set[int] = set()
    for question in value["questions"]:
        if not isinstance(question, dict):
            raise ProposalError("Each question must be an object")
        fields = {"id", "type", "prompt", "target_region", "view", "answer",
                  "rationale", "evidence_basis"}
        if fields - question.keys():
            raise ProposalError("Question is missing required fields")
        if type(question["id"]) is not int or question["id"] in ids:
            raise ProposalError("Question IDs must be unique integers")
        ids.add(question["id"])
        if question["type"] not in TYPES or question["view"] not in VIEWS:
            raise ProposalError("Unsupported question type or view")
        if question["target_region"] not in allowlist or question["target_region"] not in LEADER_ENDPOINTS:
            raise ProposalError(f"Unknown anatomical region: {question['target_region']}")
        if not str(question["answer"]).strip():
            raise ProposalError("Every question requires an answer")
        for field in ("prompt", "answer", "rationale", "evidence_basis"):
            if not isinstance(question[field], str) or len(question[field]) > 1000:
                raise ProposalError("Question text has an invalid type or length")
    for strength in value["strengths"]:
        if not isinstance(strength, dict) or strength.get("region") not in allowlist:
            raise ProposalError("Strength region is not in the local allowlist")
    for need in value["needs_practice"]:
        if not isinstance(need, dict) or need.get("priority") not in PRIORITIES:
            raise ProposalError("Invalid practice priority")
    for pair in value["confusion_pairs"]:
        if (not isinstance(pair, dict) or pair.get("requested") not in allowlist
                or pair.get("selected") not in allowlist):
            raise ProposalError("Confusion pair is not in the local allowlist")
    return value


class FiveRoundBackboard:
    def __init__(self, settings, database, transport=None):
        self.settings, self.database, self.transport = settings, database, transport

    async def _request(self, method: str, path: str, **kwargs) -> dict:
        if not self.settings.backboard_api_key:
            raise RuntimeError("Backboard credentials are missing or invalid")
        timeout = httpx.Timeout(connect=10, read=180, write=30, pool=10)
        async with httpx.AsyncClient(
                base_url=self.settings.backboard_base_url, timeout=timeout,
                transport=self.transport) as client:
            response = await client.request(
                method, path, headers={"X-API-Key": self.settings.backboard_api_key}, **kwargs)
            response.raise_for_status()
        value = response.json()
        if not isinstance(value, dict):
            raise RuntimeError("Backboard returned an invalid response")
        return value

    async def discover(self) -> dict:
        # Catalogue lookup is read-only/non-generative. Pagination prevents a false absence.
        found: list[dict] = []
        skip = 0
        while True:
            page = await self._request("GET", "/models", params={
                "model_type": "llm", "skip": skip, "limit": 500})
            models = page.get("models", [])
            if not isinstance(models, list):
                raise RuntimeError("Backboard model catalogue format is invalid")
            found.extend(x for x in models if isinstance(x, dict))
            skip += len(models)
            if not models or skip >= int(page.get("total") or skip):
                break
        result = discover_gpt55({"models": found})
        if self.database.is_five_round_demo():
            with self.database.connect() as con:
                con.execute("UPDATE backboard_demo_state SET model_discovery_operations=model_discovery_operations+1,total_operations=total_operations+1 WHERE id=1")
        return result

    def state(self) -> dict:
        if not self.database.is_five_round_demo():
            raise RuntimeError("Backboard workflow is restricted to the five-round synthetic demo")
        with self.database.connect() as con:
            return dict(con.execute("SELECT * FROM backboard_demo_state WHERE id=1").fetchone())

    async def generate(self, provider: str, model_name: str, *, regenerate: bool = False) -> dict:
        state = self.state()
        if state["status"] == "completed" and not regenerate:
            return self.cached_result()
        summary = self.database.compact_student1_summary()
        summary_json = json.dumps(summary, sort_keys=True, separators=(",", ":"))
        summary_hash = sha256_bytes(summary_json.encode())
        assistant_id, thread_id = state["assistant_id"], state["thread_id"]
        storage_ops = 0
        discovery_ops = int(state.get("model_discovery_operations", 0))
        # Deliberately no retries: ambiguous timeouts must be resolved by the presenter.
        for round_row in summary["rounds"]:
            payload = {
                "content": json.dumps({
                    "synthetic": True, "student_label": "Synthetic Student 1",
                    "round_summary": round_row}, sort_keys=True),
                "send_to_llm": "false", "stream": False, "memory": "Auto",
                "web_search": "off", "metadata": {
                    "synthetic_demo": True, "round": round_row["round"]}}
            if thread_id:
                payload["thread_id"] = thread_id
            elif assistant_id:
                payload["assistant_id"] = assistant_id
            stored = await self._request("POST", "/threads/messages", json=payload)
            assistant_id = str(stored.get("assistant_id") or assistant_id)
            thread_id = str(stored.get("thread_id") or thread_id)
            storage_ops += 1
            with self.database.connect() as con:
                con.execute("""UPDATE backboard_demo_state SET status='storing',
                  assistant_id=?,thread_id=?,storage_operations=?,
                  total_operations=?,summary_hash=? WHERE id=1""",
                  (assistant_id, thread_id, storage_ops, discovery_ops + storage_ops, summary_hash))
        allowlist = {row["region"] for row in self.database.region_rows()}
        prompt = {
            "task": "Create a tailored fictional assessment specification as strict JSON.",
            "constraints": {
                "synthetic": True, "student_label": "Synthetic Student 1",
                "model_field_must_equal": model_name, "question_count": "8-10",
                "allowed_regions": sorted(allowlist), "allowed_views": sorted(VIEWS),
                "no_urls_html_code_or_external_images": True,
                "focus": "persistent left/right misconception with strong-region controls"},
            "required_schema": {
                "synthetic": True, "student_label": "Synthetic Student 1", "model": model_name,
                "five_round_summary": "string",
                "strengths": [{"region": "allowlisted region", "evidence": "string"}],
                "needs_practice": [{"skill": "string", "evidence": "string",
                                    "priority": "high|medium|low"}],
                "confusion_pairs": [{"requested": "region", "selected": "region",
                                     "evidence_count": 0, "rounds_observed": [1]}],
                "progression": "string", "assessment_strategy": "string",
                "questions": [{"id": 1, "type": "diagram_label|left_right|matching|short_answer",
                               "prompt": "string", "target_region": "allowlisted region",
                               "view": "front|left|right|superior", "answer": "string",
                               "rationale": "string", "evidence_basis": "string"}]},
            "local_five_round_summary": summary,
        }
        generated = await self._request("POST", "/threads/messages", json={
            "thread_id": thread_id, "assistant_id": assistant_id,
            "content": json.dumps(prompt, sort_keys=True), "stream": False,
            "memory": "Readonly", "memory_response_citation": False,
            "web_search": "off", "json_output": True, "send_to_llm": "true",
            "llm_provider": provider, "model_name": model_name,
            "metadata": {"synthetic_demo": True, "summary_hash": summary_hash}})
        returned_provider = str(generated.get("model_provider") or "")
        returned_model = str(generated.get("model_name") or "")
        if returned_provider and returned_provider != provider:
            raise ProposalError("Returned provider metadata does not match the confirmed provider")
        if returned_model and returned_model != model_name:
            raise ProposalError("Returned model metadata does not match the confirmed model")
        proposal = validate_proposal(generated.get("content", ""), allowlist, model_name)
        response_json = json.dumps(proposal, sort_keys=True, separators=(",", ":"))
        response_hash = sha256_bytes(response_json.encode())
        completed = datetime.now(timezone.utc)
        from .backboard_pdf import generate_backboard_pdf as generate_compact_pdf
        pdf = generate_compact_pdf(proposal, provider, model_name, PDF_ROOT, completed)
        pdf_hash = sha256_bytes(pdf.read_bytes())
        with self.database.connect() as con:
            con.execute("""UPDATE backboard_demo_state SET status='completed',provider=?,
              model_name=?,memory_mode=?,assistant_id=?,thread_id=?,storage_operations=?,
              generation_operations=1,total_operations=?,completed_utc=?,summary_hash=?,
              response_hash=?,response_json=?,pdf_filename=?,pdf_sha256=? WHERE id=1""",
              (provider, model_name, "Memory Lite Auto storage / Readonly generation",
               assistant_id, thread_id, storage_ops, discovery_ops + storage_ops + 1,
               completed.isoformat().replace("+00:00", "Z"), summary_hash, response_hash,
               response_json, pdf.name, pdf_hash))
        receipt = {
            "timestamp": completed.isoformat().replace("+00:00", "Z"),
            "student_label": "Synthetic Student 1", "five_round_summary_hash": summary_hash,
            "assistant_id": assistant_id, "thread_id": thread_id,
            "provider": provider, "model": model_name,
            "memory_mode": "Memory Lite Auto storage / Readonly generation",
            "model_catalogue_operation_count": discovery_ops,
            "storage_operation_count": storage_ops, "llm_generation_count": 1,
            "total_backboard_operations": discovery_ops + storage_ops + 1,
            "validated_response_hash": response_hash,
            "pdf_filename": pdf.name, "pdf_sha256": pdf_hash, "outcome": "completed"}
        RECEIPT_ROOT.mkdir(parents=True, exist_ok=True)
        receipt_path = RECEIPT_ROOT / f"backboard_gpt55_receipt_{completed.strftime('%Y%m%dT%H%M%SZ')}.json"
        receipt_path.write_text(json.dumps(receipt, indent=2) + "\n", encoding="utf-8")
        result = self.cached_result()
        result["receiptPath"] = str(receipt_path)
        return result

    def cached_result(self) -> dict:
        state = self.state()
        if state["status"] != "completed":
            raise RuntimeError("No successful cached Backboard result is available")
        return {
            "cached": True, "synthetic": True, "provider": state["provider"],
            "model": state["model_name"], "memoryMode": state["memory_mode"],
            "storageOperations": state["storage_operations"],
            "generationOperations": state["generation_operations"],
            "totalOperations": state["total_operations"],
            "completedUtc": state["completed_utc"], "pdfSha256": state["pdf_sha256"],
            "filename": state["pdf_filename"],
            "downloadUrl": f"/api/v1/teacher/backboard-tests/{state['pdf_filename']}"}


def generate_backboard_pdf(proposal: dict, provider: str, model: str,
                           output_dir: Path, now: datetime) -> Path:
    from reportlab.lib import colors
    from reportlab.lib.pagesizes import letter
    from reportlab.lib.styles import getSampleStyleSheet
    from reportlab.lib.units import inch
    from reportlab.platypus import Image, PageBreak, Paragraph, SimpleDocTemplate, Spacer, Table, TableStyle
    output_dir.mkdir(parents=True, exist_ok=True)
    stamp = now.strftime("%Y%m%dT%H%M%S_%fZ")
    path = output_dir / f"Synthetic_Student_1_GPT55_Backboard_Tailored_Jaw_Test_{stamp}.pdf"
    jaw = render_jaw(output_dir / "assets" / "project_jaw_backboard.png", 18)
    styles = getSampleStyleSheet()
    story = [
        Paragraph("Tailored Jaw Assessment — SYNTHETIC DEMONSTRATION", styles["Title"]),
        Paragraph("Synthetic Student 1 • five fictional rounds", styles["Heading2"]),
        Paragraph("Assessment specification generated through Backboard. Jaw imagery and PDF rendering performed locally.", styles["BodyText"]),
        Paragraph(f"Provider/model: {provider} / {model}", styles["BodyText"]), Spacer(1, 8),
        Image(str(jaw), width=7 * inch, height=4.04 * inch)]
    for q in proposal["questions"]:
        story.append(Paragraph(f"<b>{q['id']}.</b> {q['prompt']}<br/>"
                               "____________________________________________________", styles["BodyText"]))
        story.append(Spacer(1, 5))
    story += [PageBreak(), Paragraph("Teacher Answer Key", styles["Title"]),
              Paragraph("SYNTHETIC DEMONSTRATION — not a diagnosis or measured learning result.",
                        styles["BodyText"])]
    rows = [["Q", "Answer", "Target", "Evidence / rationale"]]
    for q in proposal["questions"]:
        rows.append([str(q["id"]), Paragraph(q["answer"], styles["BodyText"]),
                     REGION_LABELS.get(q["target_region"], q["target_region"]),
                     Paragraph(q["evidence_basis"] + " " + q["rationale"], styles["BodyText"])])
    table = Table(rows, colWidths=[.3*inch,1.8*inch,1.5*inch,3.3*inch], repeatRows=1)
    table.setStyle(TableStyle([
        ("BACKGROUND", (0, 0), (-1, 0), colors.HexColor("#12344d")),
        ("TEXTCOLOR", (0, 0), (-1, 0), colors.white),
        ("GRID", (0, 0), (-1, -1), .4, colors.grey),
        ("VALIGN", (0, 0), (-1, -1), "TOP")]))
    story.append(table)
    def footer(canvas, doc):
        canvas.saveState()
        canvas.setFillColor(colors.Color(.78, .18, .18, alpha=.09))
        canvas.setFont("Helvetica-Bold", 38)
        canvas.translate(letter[0]/2, letter[1]/2)
        canvas.rotate(32)
        canvas.drawCentredString(0, 0, "SYNTHETIC DEMONSTRATION")
        canvas.restoreState()
    SimpleDocTemplate(str(path), pagesize=letter, leftMargin=.55*inch, rightMargin=.55*inch,
                      topMargin=.45*inch, bottomMargin=.5*inch).build(
                          story, onFirstPage=footer, onLaterPages=footer)
    return path
