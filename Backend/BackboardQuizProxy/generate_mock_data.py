#!/usr/bin/env python3
from datetime import datetime, timezone
from pathlib import Path
from uuid import uuid5, NAMESPACE_URL
from app.config import Settings
from app.database import Database

settings=Settings.from_environment(); db=Database(settings.database_path)
regions=[("LeftMentalForamen","MentalProtuberance"),("RightRamus","RightRamus"),
         ("LowerIncisors","MentalProtuberance"),("LeftCondylarProcess","LeftCoronoidProcess")]
for s in range(1,4):
    student=f"student_{s:03d}"
    for i in range(12):
        expected,selected=regions[(i+s)%len(regions)]; correct=expected==selected or (i+s)%4==0
        if correct: selected=expected
        eid=uuid5(NAMESPACE_URL,f"jaw-mock-{student}-{i}")
        db.insert_attempt({"eventId":str(eid),"studentId":student,"sessionId":f"mock_session_{s}",
          "questionId":f"mock_q_{(i+s)%len(regions)}","objectId":"jaw","regionMapVersion":"mock-v1",
          "expectedStableRegionId":expected,"selectedStableRegionId":selected,"correct":correct,
          "responseTimeSeconds":4.5+(i%5)*1.7,"attemptNumber":1+(i%2),"hintLevel":1 if i%3==0 else 0,
          "utcTimestamp":datetime.now(timezone.utc).isoformat()})
print(f"Mock data ready in {settings.database_path}")
