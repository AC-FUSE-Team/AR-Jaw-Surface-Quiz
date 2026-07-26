from __future__ import annotations

import math
from datetime import datetime, timezone
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont
from reportlab.lib import colors
from reportlab.lib.enums import TA_CENTER
from reportlab.lib.pagesizes import letter
from reportlab.lib.styles import ParagraphStyle, getSampleStyleSheet
from reportlab.lib.units import inch
from reportlab.platypus import (PageBreak, Paragraph, SimpleDocTemplate, Spacer,
                                Table, TableStyle)

PROXY_ROOT = Path(__file__).resolve().parents[1]
PROJECT_ROOT = PROXY_ROOT.parents[1]
MESH_PATH = PROJECT_ROOT / "Assets/JawAR/Models/JawMarkerAlignedUnity.obj"
DEFAULT_OUTPUT = PROJECT_ROOT / "Artifacts" / "SyntheticTeacherDemo_2026-07-24" / "pdf"
FIVE_ROUND_OUTPUT = PROJECT_ROOT / "Artifacts" / "SyntheticTeacherDemo_5Rounds_2026-07-24" / "pdf"
REGION_LABELS = {
    "LeftRamus": "Left Ramus", "RightRamus": "Right Ramus",
    "LeftCondylarProcess": "Left Condylar Process",
    "RightCondylarProcess": "Right Condylar Process",
    "LeftMentalForamen": "Left Mental Foramen",
    "RightMentalForamen": "Right Mental Foramen",
    "MentalProtuberance": "Mental Protuberance", "LowerIncisors": "Lower Incisors",
}
QUESTIONS = [
    (1, "LeftRamus", "Identify the subject's left ramus.", "Left/right discrimination"),
    (2, "RightRamus", "Identify the subject's right ramus.", "Left/right discrimination"),
    (3, "LeftCondylarProcess", "Identify the subject's left condylar process.", "Bilateral process"),
    (4, "RightCondylarProcess", "Identify the subject's right condylar process.", "Bilateral process"),
    (5, "LeftMentalForamen", "Identify the subject's left mental foramen.", "Bilateral landmark"),
    (6, "RightMentalForamen", "Identify the subject's right mental foramen.", "Bilateral landmark"),
    (7, "MentalProtuberance", "Name the non-lateralized structure indicated.", "Control structure"),
    (8, "LowerIncisors", "Name the anterior teeth indicated.", "Control structure"),
    (9, "LeftRamus", "For label 1, circle the subject's side: LEFT / RIGHT", "Perspective check"),
    (10, "RightRamus", "Explain how anatomical left/right differs from the viewer's left/right.", "Perspective explanation"),
]


def _mesh() -> tuple[list[tuple[float, float, float]], list[tuple[int, int, int]]]:
    vertices, faces = [], []
    with MESH_PATH.open(encoding="utf-8") as handle:
        for line in handle:
            if line.startswith("v "):
                _, x, y, z = line.split()[:4]
                vertices.append((float(x), float(y), float(z)))
            elif line.startswith("f "):
                indices = [int(part.split("/")[0]) - 1 for part in line.split()[1:4]]
                faces.append(tuple(indices))
    if not vertices or not faces:
        raise RuntimeError("Project jaw mesh could not be read")
    return vertices, faces


def render_jaw(path: Path, angle_degrees: float = 0) -> Path:
    """Produce a neutral grayscale render directly from the project-owned OBJ mesh."""
    vertices, faces = _mesh()
    angle = math.radians(angle_degrees)
    transformed = []
    for x, y, z in vertices:
        u = x * math.cos(angle) + z * math.sin(angle)
        depth = -x * math.sin(angle) + z * math.cos(angle)
        transformed.append((u, y, depth))
    us, ys = [p[0] for p in transformed], [p[1] for p in transformed]
    min_u, max_u, min_y, max_y = min(us), max(us), min(ys), max(ys)
    width, height, margin = 900, 520, 28
    scale = min((width - 2 * margin) / (max_u - min_u),
                (height - 2 * margin) / (max_y - min_y))
    def point(index: int) -> tuple[int, int]:
        u, y, _ = transformed[index]
        return (int((u - min_u) * scale + margin),
                int(height - ((y - min_y) * scale + margin)))
    image = Image.new("RGB", (width, height), "white")
    draw = ImageDraw.Draw(image)
    ordered = sorted(faces[::5], key=lambda f: sum(transformed[i][2] for i in f) / 3)
    for face in ordered:
        p = [point(i) for i in face]
        shade = int(220 - 55 * ((sum(transformed[i][2] for i in face) / 3 - min(p[0] for p in transformed)) if False else .4))
        draw.polygon(p, fill=(shade, shade, shade), outline=(130, 130, 130))
    draw.rectangle((0, 0, width - 1, height - 1), outline=(205, 210, 214), width=2)
    path.parent.mkdir(parents=True, exist_ok=True)
    image.save(path, dpi=(180, 180))
    return path


def _footer(canvas, doc, created: str) -> None:
    canvas.saveState()
    canvas.setFillColor(colors.Color(.78, .18, .18, alpha=.09))
    canvas.setFont("Helvetica-Bold", 46)
    canvas.translate(letter[0] / 2, letter[1] / 2)
    canvas.rotate(32)
    canvas.drawCentredString(0, 0, "SYNTHETIC DEMO")
    canvas.rotate(-32)
    canvas.translate(-letter[0] / 2, -letter[1] / 2)
    canvas.setFillColor(colors.HexColor("#4b5563"))
    canvas.setFont("Helvetica", 8)
    canvas.drawString(.55 * inch, .35 * inch, f"Created locally: {created}")
    canvas.drawRightString(letter[0] - .55 * inch, .35 * inch, f"Page {doc.page}")
    canvas.restoreState()


def generate_tailored_pdf(database, output_dir: Path | None = None,
                           now: datetime | None = None) -> Path:
    if not database.is_synthetic_demo():
        raise ValueError("Tailored PDF generation is restricted to the synthetic demonstration")
    detail = database.student_detail("student_synthetic_1")
    if not detail or not detail.get("synthetic"):
        raise ValueError("Synthetic Student 1 is unavailable")
    output_dir = Path(output_dir or (FIVE_ROUND_OUTPUT if getattr(database, "is_five_round_demo", lambda: False)() else DEFAULT_OUTPUT))
    output_dir.mkdir(parents=True, exist_ok=True)
    now = now or datetime.now(timezone.utc)
    stamp = now.strftime("%Y%m%dT%H%M%S_%fZ")
    path = output_dir / f"Synthetic_Student_1_Tailored_Jaw_Test_{stamp}.pdf"
    if path.exists():
        raise FileExistsError(path)
    render = render_jaw(output_dir / "assets" / "project_jaw_front_oblique.png", 18)
    created = now.strftime("%Y-%m-%d %H:%M:%S UTC")
    styles = getSampleStyleSheet()
    title = ParagraphStyle("TitleDemo", parent=styles["Title"], fontName="Helvetica-Bold",
                           fontSize=20, leading=24, textColor=colors.HexColor("#12344d"),
                           alignment=TA_CENTER, spaceAfter=5)
    subtitle = ParagraphStyle("Subtitle", parent=styles["Normal"], fontSize=11, leading=14,
                              alignment=TA_CENTER, textColor=colors.HexColor("#425466"))
    body = ParagraphStyle("BodyDemo", parent=styles["BodyText"], fontSize=9, leading=12)
    small = ParagraphStyle("SmallDemo", parent=body, fontSize=7.5, leading=9.5)
    doc = SimpleDocTemplate(str(path), pagesize=letter, leftMargin=.55 * inch,
                            rightMargin=.55 * inch, topMargin=.48 * inch,
                            bottomMargin=.55 * inch, title="Tailored Lower-Jaw Assessment — Synthetic Demonstration")
    story = [Paragraph("Tailored Lower-Jaw Assessment", title),
             Paragraph("Synthetic Demonstration — Student 1", subtitle), Spacer(1, 6),
             Table([["Student name: ______________________________", "Date: __________________"]],
                   colWidths=[4.2 * inch, 2.1 * inch], style=[("FONT", (0, 0), (-1, -1), "Helvetica", 9),
                                                              ("BOTTOMPADDING", (0, 0), (-1, -1), 5)]),
             Paragraph("Anatomical left and right are always from the subject's perspective. In a front view, the subject's left appears on the viewer's right.", body), Spacer(1, 6)]
    # Overlay eight clear callouts on the mesh image using a ReportLab Drawing-like table canvas image.
    callout = Image.open(render).convert("RGB")
    draw = ImageDraw.Draw(callout)
    targets = {1:(690,235),2:(80,235),3:(650,80),4:(55,80),5:(610,345),6:(185,345),7:(450,425),8:(450,330)}
    labels = {1:(825,210),2:(180,210),3:(820,48),4:(185,48),5:(780,365),6:(100,365),7:(540,480),8:(360,455)}
    label_font = ImageFont.truetype(str(PROJECT_ROOT / "Assets/JawAR/Quiz/Material3/Fonts/Roboto-Bold.ttf"), 24)
    for number, target in targets.items():
        label = labels[number]
        draw.line((label, target), fill=(20, 36, 48), width=5)
        draw.ellipse((label[0]-18,label[1]-18,label[0]+18,label[1]+18), fill="white", outline=(10,25,35), width=4)
        text = str(number)
        box = draw.textbbox((0, 0), text, font=label_font)
        draw.text((label[0]-(box[2]-box[0])/2,label[1]-(box[3]-box[1])/2-3), text, font=label_font, fill=(0,0,0))
        draw.ellipse((target[0]-7,target[1]-7,target[0]+7,target[1]+7), fill="white", outline=(0,0,0), width=3)
    annotated = output_dir / "assets" / "project_jaw_annotated.png"
    callout.save(annotated, dpi=(180,180))
    story += [Table([["FRONT-OBLIQUE VIEW — project-owned lower-jaw mesh"]], colWidths=[7.0*inch],
                    style=[("ALIGN",(0,0),(-1,-1),"CENTER"),("FONT",(0,0),(-1,-1),"Helvetica-Bold",8),
                           ("TEXTCOLOR",(0,0),(-1,-1),colors.HexColor("#425466"))]),
              Table([[str(annotated)]], colWidths=[7.0*inch], rowHeights=[3.45*inch],
                    style=[("BACKGROUND",(0,0),(-1,-1),colors.white)])]
    # Replace string cell with ReportLab image after table construction.
    from reportlab.platypus import Image as RLImage
    story[-1] = RLImage(str(annotated), width=7.0*inch, height=4.04*inch)
    qrows = []
    for i in range(0, len(QUESTIONS), 2):
        left = QUESTIONS[i]
        right = QUESTIONS[i + 1]
        qrows.append([Paragraph(f"<b>{left[0]}.</b> {left[2]}<br/>________________________________", small),
                      Paragraph(f"<b>{right[0]}.</b> {right[2]}<br/>________________________________", small)])
    qtable = Table(qrows, colWidths=[3.48*inch,3.48*inch], rowHeights=[.34*inch]*5)
    qtable.setStyle(TableStyle([("VALIGN",(0,0),(-1,-1),"TOP"),("BOX",(0,0),(-1,-1),.5,colors.HexColor("#b7c1c9")),
                                ("INNERGRID",(0,0),(-1,-1),.35,colors.HexColor("#d4dbe0")),
                                ("LEFTPADDING",(0,0),(-1,-1),6),("RIGHTPADDING",(0,0),(-1,-1),6)]))
    story += [Spacer(1,5), qtable, PageBreak(), Paragraph("Teacher Answer Key & Rationale", title),
              Paragraph("SYNTHETIC DEMONSTRATION DATA — fictional interactions; not measured study results", subtitle), Spacer(1,8)]
    evidence = (f"Local evidence: {detail['left_right_confusion_count']} exact left/right reversals across "
                f"{detail['lateralized_first_attempts']} lateralized first attempts "
                f"({detail['left_right_confusion_rate']:.0%}). Overall accuracy {detail['accuracy']:.0%}; "
                f"first-attempt accuracy {detail['first_attempt_accuracy']:.0%}. The assessment emphasizes "
                "bilateral discrimination while retaining two strong non-lateralized controls.")
    story += [Paragraph(evidence, body), Spacer(1,8)]
    answer_rows = [["Q", "Correct answer", "Skill checked", "Why selected"]]
    for number, region, _, skill in QUESTIONS:
        if number <= 8:
            answer = REGION_LABELS[region]
        elif number == 9:
            answer = "LEFT"
        else:
            answer = "The subject's left/right can be opposite the viewer's in a front view."
        rationale = "Targets the recurring fictional left/right reversal." if number not in (7,8) else "Control item: demonstrated strength on a non-lateralized structure."
        answer_rows.append([str(number), Paragraph(answer, small), Paragraph(skill, small), Paragraph(rationale, small)])
    table = Table(answer_rows, colWidths=[.3*inch,1.55*inch,1.35*inch,3.7*inch], repeatRows=1)
    table.setStyle(TableStyle([("BACKGROUND",(0,0),(-1,0),colors.HexColor("#12344d")),
                               ("TEXTCOLOR",(0,0),(-1,0),colors.white),("FONT",(0,0),(-1,0),"Helvetica-Bold",8),
                               ("GRID",(0,0),(-1,-1),.45,colors.HexColor("#b7c1c9")),
                               ("VALIGN",(0,0),(-1,-1),"TOP"),("ROWBACKGROUNDS",(0,1),(-1,-1),[colors.white,colors.HexColor("#f4f7f9")])]))
    story += [table, Spacer(1,8), Paragraph("Teacher note: This report describes only a designed pattern in synthetic quiz events. It does not imply a diagnosis, disability, or scientifically validated learning conclusion.", body),
              Spacer(1,7), Paragraph("SYNTHETIC DEMONSTRATION DATA — These are fictional student interactions created to demonstrate the teacher workflow. They are not real learners or measured study results.", body)]
    doc.build(story, onFirstPage=lambda c,d:_footer(c,d,created), onLaterPages=lambda c,d:_footer(c,d,created))
    return path
