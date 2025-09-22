from fastapi import FastAPI
from pydantic import BaseModel
import uvicorn
import asyncio

app = FastAPI(title="ACA Workshop API", version="1.0.0")

class SlowResponse(BaseModel):
    status: str
    detail: str

@app.get("/")
async def root():
    return {"message": "Hello from Container Apps (Python)!"}

@app.get("/healthz")
async def health():
    return {"status": "OK"}

@app.get("/slow", response_model=SlowResponse)
async def slow():
    await asyncio.sleep(0.5)
    return SlowResponse(status="done", detail="Completed after simulated delay")

if __name__ == "__main__":
    uvicorn.run(app, host="0.0.0.0", port=8080)
