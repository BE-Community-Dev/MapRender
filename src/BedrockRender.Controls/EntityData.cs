namespace BedrockRender.Controls
{
    public readonly struct EntityData
    {
        public readonly string Identifier;
        public readonly double X;
        public readonly double Z;

        public EntityData(string identifier, double x, double z)
        {
            Identifier = identifier;
            X = x;
            Z = z;
        }
    }
}
