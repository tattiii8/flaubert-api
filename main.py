from fastapi import FastAPI, HTTPException
from pydantic import BaseModel
from typing import List, Optional
import aiomysql
import os

app = FastAPI(title="Flaubert API", version="1.0.0")

# DB接続設定（環境変数から取得）
DB_CONFIG = {
    "host": os.getenv("DB_HOST", "localhost"),
    "port": int(os.getenv("DB_PORT", 3306)),
    "user": os.getenv("DB_USER", "redmine"),
    "password": os.getenv("DB_PASSWORD", ""),
    "db": os.getenv("DB_NAME", "redmine"),
}


class User(BaseModel):
    id: int
    login: str
    firstname: str
    lastname: str
    admin: bool
    status: int


async def get_db():
    conn = await aiomysql.connect(**DB_CONFIG)
    return conn


@app.get("/")
def root():
    return {"message": "Bonjour! from FastAPI!"}


@app.get("/health")
def health():
    return {"status": "ok"}

@app.get("/flaubert")
def root():
    return {"message": "This is Flaubert API"}


@app.get("/users", response_model=List[User])
async def get_users():
    conn = await get_db()
    try:
        async with conn.cursor(aiomysql.DictCursor) as cur:
            await cur.execute(
                "SELECT id, login, firstname, lastname, admin, status FROM users"
            )
            rows = await cur.fetchall()
            return rows
    finally:
        conn.close()


@app.get("/users/{user_id}", response_model=User)
async def get_user(user_id: int):
    conn = await get_db()
    try:
        async with conn.cursor(aiomysql.DictCursor) as cur:
            await cur.execute(
                "SELECT id, login, firstname, lastname, admin, status FROM users WHERE id = %s",
                (user_id,),
            )
            row = await cur.fetchone()
            if row is None:
                raise HTTPException(status_code=404, detail="User not found")
            return row
    finally:
        conn.close()