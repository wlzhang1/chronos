import os
from dotenv import load_dotenv

load_dotenv(override=True)


class Settings:
    PROJECT_NAME: str = "Chronos API"
    VERSION: str = "0.1.0"

    @property
    def GEMINI_API_KEY(self) -> str:
        load_dotenv(override=True)
        return os.getenv("GEMINI_API_KEY", "")

    @property
    def GEMINI_MODEL(self) -> str:
        load_dotenv(override=True)
        return os.getenv("GEMINI_MODEL", "gemini-2.0-flash")

    @property
    def GOOGLE_GENAI_USE_VERTEXAI(self) -> bool:
        return os.getenv("GOOGLE_GENAI_USE_VERTEXAI", "").lower() in ("true", "1", "yes")

    @property
    def FRONTEND_URL(self) -> str:
        load_dotenv(override=True)
        return os.getenv("FRONTEND_URL", "http://localhost:3000")

    @property
    def GOOGLE_CLIENT_ID(self) -> str:
        load_dotenv(override=True)
        return os.getenv("GOOGLE_CLIENT_ID", "")

    @property
    def GOOGLE_CLIENT_SECRET(self) -> str:
        load_dotenv(override=True)
        return os.getenv("GOOGLE_CLIENT_SECRET", "")

    @property
    def GOOGLE_REDIRECT_URI(self) -> str:
        load_dotenv(override=True)
        return os.getenv(
            "GOOGLE_REDIRECT_URI", "http://localhost:8000/api/auth/callback"
        )


settings = Settings()


