from fastapi import FastAPI, HTTPException, Query
from pydantic import BaseModel, Field
from typing import List, Optional
import aiomysql
import os
import hashlib
import secrets

app = FastAPI(title="Flaubert API", version="1.0.0")

# ── DB接続設定（環境変数から取得） ────────────────────────────────────────────
DB_CONFIG = {
    "host":     os.getenv("DB_HOST", "localhost"),
    "port":     int(os.getenv("DB_PORT", 3306)),
    "user":     os.getenv("DB_USER", "redmine"),
    "password": os.getenv("DB_PASSWORD", ""),
    "db":       os.getenv("DB_NAME", "redmine"),
}

# ── パスワードハッシュ (Redmine互換: SHA1(salt + SHA1(password))) ─────────────
def _sha1(s: str) -> str:
    return hashlib.sha1(s.encode()).hexdigest()

def generate_salt() -> str:
    return secrets.token_hex(32)  # 64文字

def hash_password(plain: str, salt: str) -> str:
    return _sha1(salt + _sha1(plain))

# ── スキーマ ──────────────────────────────────────────────────────────────────
class User(BaseModel):
    id: int
    login: str
    firstname: str
    lastname: str
    admin: bool
    status: int

class UserCreate(BaseModel):
    login:             str   = Field(..., min_length=1, max_length=255)
    password:          str   = Field(..., min_length=1)
    firstname:         str   = Field(..., min_length=1, max_length=30)
    lastname:          str   = Field(..., min_length=1, max_length=255)
    language:          str   = Field(default="ja", max_length=5)
    admin:             bool  = False
    status:            int   = 1         # 1=active, 3=locked
    mail_notification: str   = Field(default="")

class UserCreated(BaseModel):
    id:         int
    login:      str
    firstname:  str
    lastname:   str
    admin:      bool
    status:     int
    language:   str

# ── DB接続ヘルパー ────────────────────────────────────────────────────────────
async def get_db():
    conn = await aiomysql.connect(**DB_CONFIG)
    return conn

# ── ルート / ヘルスチェック ───────────────────────────────────────────────────
@app.get("/")
def root():
    return {"message": "Bonjour! from FastAPI!"}

@app.get("/api/v1/health")
def health():
    return {"status": "ok"}

# ── GET /api/v1/users ─────────────────────────────────────────────────────────
@app.get("/api/v1/users", response_model=List[User])
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

# ── GET /api/v1/users/{user_id} ───────────────────────────────────────────────
@app.get("/api/v1/users/{user_id}", response_model=User)
async def get_user(user_id: int):
    conn = await get_db()
    try:
        async with conn.cursor(aiomysql.DictCursor) as cur:
            await cur.execute(
                "SELECT id, login, firstname, lastname, admin, status"
                " FROM users WHERE id = %s",
                (user_id,),
            )
            row = await cur.fetchone()
            if row is None:
                raise HTTPException(status_code=404, detail="User not found")
            return row
    finally:
        conn.close()

# ── POST /api/v1/users ────────────────────────────────────────────────────────
@app.post("/api/v1/users", response_model=UserCreated, status_code=201)
async def create_user(body: UserCreate):
    conn = await get_db()
    try:
        async with conn.cursor(aiomysql.DictCursor) as cur:

            # ── ログイン重複チェック ───────────────────────────────────────
            await cur.execute(
                "SELECT id FROM users WHERE login = %s", (body.login,)
            )
            if await cur.fetchone():
                raise HTTPException(status_code=409, detail="login already taken")

            # ── パスワードハッシュ ─────────────────────────────────────────
            salt            = generate_salt()
            hashed_password = hash_password(body.password, salt)

            # ── INSERT ────────────────────────────────────────────────────
            await cur.execute(
                """
                INSERT INTO users
                  (login, hashed_password, firstname, lastname,
                   admin, status, language, mail_notification,
                   salt, must_change_passwd, created_on, updated_on, type)
                VALUES
                  (%s, %s, %s, %s,
                   %s, %s, %s, %s,
                   %s, 0, NOW(), NOW(), 'User')
                """,
                (
                    body.login, hashed_password, body.firstname, body.lastname,
                    int(body.admin), body.status, body.language, body.mail_notification,
                    salt,
                ),
            )
            await conn.commit()
            new_id = cur.lastrowid

        return UserCreated(
            id=new_id,
            login=body.login,
            firstname=body.firstname,
            lastname=body.lastname,
            admin=body.admin,
            status=body.status,
            language=body.language,
        )
    finally:
        conn.close()

# ── DELETE /api/v1/users/{user_id} ───────────────────────────────────────────
@app.delete("/api/v1/users/{user_id}", status_code=200)
async def delete_user(
    user_id: int,
    soft: bool = Query(default=False, description="Trueの場合はstatus=3(locked)に更新"),
):
    conn = await get_db()
    try:
        async with conn.cursor(aiomysql.DictCursor) as cur:

            # ── 存在チェック ──────────────────────────────────────────────
            await cur.execute(
                "SELECT id, login FROM users WHERE id = %s", (user_id,)
            )
            row = await cur.fetchone()
            if row is None:
                raise HTTPException(status_code=404, detail="User not found")

            if soft:
                # 論理削除: status を 3 (locked) に変更
                await cur.execute(
                    "UPDATE users SET status = 3, updated_on = NOW() WHERE id = %s",
                    (user_id,),
                )
                await conn.commit()
                return {"message": f"User {user_id} locked (soft delete)"}
            else:
                # 物理削除
                await cur.execute("DELETE FROM users WHERE id = %s", (user_id,))
                await conn.commit()
                return {"message": f"User {user_id} deleted"}
    finally:
        conn.close()
