from __future__ import annotations

from datetime import datetime
from pathlib import Path

from PIL import Image as PILImage, ImageDraw, ImageFont
from reportlab.lib import colors
from reportlab.lib.enums import TA_CENTER
from reportlab.lib.pagesizes import letter
from reportlab.lib.styles import ParagraphStyle, getSampleStyleSheet
from reportlab.lib.units import inch
from reportlab.platypus import (Image, PageBreak, Paragraph, SimpleDocTemplate,
                                Spacer, Table, TableStyle)

from .synthetic_pdf import PROJECT_ROOT, REGION_LABELS, render_jaw

LEADER_ENDPOINTS = {
    "LeftRamus": (690, 235), "RightRamus": (80, 235),
    "LeftCondylarProcess": (650, 80), "RightCondylarProcess": (55, 80),
    "LeftCoronoidProcess": (735, 145), "RightCoronoidProcess": (125, 145),
    "LeftMentalForamen": (610, 345), "RightMentalForamen": (185, 345),
    "MentalProtuberance": (450, 425), "LowerIncisors": (450, 330),
}
LABEL_POINTS = [
    (810, 245), (90, 245), (790, 55), (120, 55), (800, 140),
    (100, 140), (760, 370), (140, 370), (550, 470), (360, 460)]


def _annotated(proposal: dict, output_dir: Path) -> Path:
    raw = render_jaw(output_dir / "assets" / "project_jaw_backboard.png", 18)
    image = PILImage.open(raw).convert("RGB")
    draw = ImageDraw.Draw(image)
    font = ImageFont.truetype(
        str(PROJECT_ROOT / "Assets/JawAR/Quiz/Material3/Fonts/Roboto-Bold.ttf"), 21)
    for question, label in zip(proposal["questions"], LABEL_POINTS):
        target = LEADER_ENDPOINTS[question["target_region"]]
        number = str(question["id"])
        draw.line((label, target), fill=(20, 36, 48), width=4)
        draw.ellipse((label[0]-16, label[1]-16, label[0]+16, label[1]+16),
                     fill="white", outline=(10, 25, 35), width=3)
        box = draw.textbbox((0, 0), number, font=font)
        draw.text((label[0]-(box[2]-box[0])/2, label[1]-(box[3]-box[1])/2-2),
                  number, font=font, fill=(0, 0, 0))
        draw.ellipse((target[0]-6, target[1]-6, target[0]+6, target[1]+6),
                     fill="white", outline=(0, 0, 0), width=2)
    path = output_dir / "assets" / "project_jaw_backboard_annotated.png"
    image.save(path, dpi=(180, 180))
    return path


def generate_backboard_pdf(proposal: dict, provider: str, model: str,
                           output_dir: Path, now: datetime) -> Path:
    output_dir.mkdir(parents=True, exist_ok=True)
    stamp = now.strftime("%Y%m%dT%H%M%S_%fZ")
    path = output_dir / f"Synthetic_Student_1_GPT55_Backboard_Tailored_Jaw_Test_{stamp}.pdf"
    jaw = _annotated(proposal, output_dir)
    styles = getSampleStyleSheet()
    title = ParagraphStyle("BTitle", parent=styles["Title"], fontSize=18, leading=21,
                           textColor=colors.HexColor("#12344d"), alignment=TA_CENTER)
    body = ParagraphStyle("BBody", parent=styles["BodyText"], fontSize=8.2, leading=10)
    tiny = ParagraphStyle("BTiny", parent=body, fontSize=7.1, leading=8.4)
    story = [
        Paragraph("Tailored Jaw Assessment", title),
        Paragraph("SYNTHETIC DEMONSTRATION — Synthetic Student 1", body),
        Paragraph(f"Specification: Backboard ({provider}/{model}) • imagery/PDF: local", body),
        Spacer(1, 4), Image(str(jaw), width=7*inch, height=4.04*inch), Spacer(1, 4)]
    question_cells = []
    questions = proposal["questions"]
    for index in range(0, len(questions), 2):
        row = []
        for q in questions[index:index+2]:
            row.append(Paragraph(
                f"<b>{q['id']}.</b> {q['prompt']}<br/>______________________________", tiny))
        if len(row) == 1:
            row.append("")
        question_cells.append(row)
    qtable = Table(question_cells, colWidths=[3.48*inch, 3.48*inch],
                   rowHeights=[.34*inch] * len(question_cells))
    qtable.setStyle(TableStyle([
        ("VALIGN", (0, 0), (-1, -1), "TOP"),
        ("GRID", (0, 0), (-1, -1), .35, colors.HexColor("#c4cfd5")),
        ("LEFTPADDING", (0, 0), (-1, -1), 5),
        ("RIGHTPADDING", (0, 0), (-1, -1), 5)]))
    story += [qtable, PageBreak(), Paragraph("Teacher Answer Key", title),
              Paragraph("SYNTHETIC DEMONSTRATION — fictional patterns, not a diagnosis or measured result.", body),
              Paragraph(f"Backboard provider/model: {provider} / {model}. Jaw imagery and PDF rendering: local.", body),
              Spacer(1, 6)]
    rows = [["Q", "Answer", "Target", "Evidence / rationale"]]
    for q in questions:
        rows.append([str(q["id"]), Paragraph(q["answer"], tiny),
                     Paragraph(REGION_LABELS.get(q["target_region"],
                                                q["target_region"]), tiny),
                     Paragraph(q["evidence_basis"] + " " + q["rationale"], tiny)])
    table = Table(rows, colWidths=[.28*inch, 1.45*inch, 1.38*inch, 3.85*inch],
                  repeatRows=1)
    table.setStyle(TableStyle([
        ("BACKGROUND", (0, 0), (-1, 0), colors.HexColor("#12344d")),
        ("TEXTCOLOR", (0, 0), (-1, 0), colors.white),
        ("FONT", (0, 0), (-1, 0), "Helvetica-Bold", 7.5),
        ("GRID", (0, 0), (-1, -1), .35, colors.HexColor("#aebbc2")),
        ("VALIGN", (0, 0), (-1, -1), "TOP"),
        ("TOPPADDING", (0, 0), (-1, -1), 3),
        ("BOTTOMPADDING", (0, 0), (-1, -1), 3)]))
    story += [table, Spacer(1, 6),
              Paragraph("The assessment specification was generated through Backboard. "
                        "Only the compact fictional five-round summary was sent. All anatomy "
                        "imagery, verified leader endpoints, and PDF layout were produced locally.",
                        body)]
    def footer(canvas, doc):
        canvas.saveState()
        canvas.setFillColor(colors.Color(.78, .18, .18, alpha=.08))
        canvas.setFont("Helvetica-Bold", 39)
        canvas.translate(letter[0]/2, letter[1]/2)
        canvas.rotate(32)
        canvas.drawCentredString(0, 0, "SYNTHETIC DEMONSTRATION")
        canvas.restoreState()
    SimpleDocTemplate(str(path), pagesize=letter, leftMargin=.52*inch,
                      rightMargin=.52*inch, topMargin=.36*inch,
                      bottomMargin=.45*inch).build(
                          story, onFirstPage=footer, onLaterPages=footer)
    return path
