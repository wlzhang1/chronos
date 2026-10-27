import type { Metadata } from "next";
import "./globals.css";

export const metadata: Metadata = {
  title: "Chronos – AI Day Planner",
  description:
    "KI-gestützter Day-Planner, der unstrukturierte Texteingaben in strukturierte Google-Kalenderereignisse übersetzt.",
};

export default function RootLayout({
  children,
}: Readonly<{
  children: React.ReactNode;
}>) {
  return (
    <html lang="de" className="dark">
      <body className="min-h-screen bg-zinc-950 text-zinc-100 antialiased selection:bg-indigo-500/30">
        {children}
      </body>
    </html>
  );
}

