#version 330 core
out vec4 FragColor;
in vec2 vUV;

uniform sampler2D currentState;
uniform sampler2D ruleTex;
uniform sampler2D interactionTex;
uniform vec2 texelSize;

void main()
{
    vec4 cell = texture(currentState, vUV);
    float alive = cell.r;
    int type = int(cell.g * 255.0 + 0.5);

    float fcount = 0.0;

    for (int dx = -1; dx <= 1; dx++)
    for (int dy = -1; dy <= 1; dy++)
    {
        if (dx == 0 && dy == 0) continue;
        vec2 offset = vec2(dx, dy) * texelSize;

        vec2 uv = fract(vUV + offset);
        vec4 neigh = texture(currentState, uv);

        if (neigh.r > 0.5)
        {
            int nType = int(neigh.g * 255.0 + 0.5);
            float weight = texelFetch(interactionTex, ivec2(nType, type), 0).r;
            fcount += weight;
        }
    }


    int n = int(round(fcount));
    n = clamp(n, 0, 8);

    vec2 rule = texelFetch(ruleTex, ivec2(n, type), 0).rg;
    float born = rule.r;
    float survive = rule.g;

    float nextAlive = alive > 0.5 ? survive : born;
    FragColor = vec4(nextAlive, cell.g, 0.0, 1.0);
}
