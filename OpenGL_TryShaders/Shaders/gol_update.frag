#version 330 core
out vec4 FragColor;

in vec2 vUV;

uniform sampler2D currentState; 
uniform sampler2D ruleTex;  
uniform vec2 texelSize;

vec4 cell(vec2 uv) { return texture(currentState, uv); }

void main()
{
    vec4 self = cell(vUV);

    int count = 0;
    for (int x = -1; x <= 1; x++)
    for (int y = -1; y <= 1; y++)
    {
        if (x == 0 && y == 0) continue;
        vec4 n = cell(vUV + vec2(x, y) * texelSize);
        if (n.r > 0.5) count++;
    }

    int type = int(self.g * 255.0 + 0.5);

    vec2 rule = texelFetch(ruleTex, ivec2(count, type), 0).rg;

    float aliveNow  = self.r > 0.5 ? 1.0 : 0.0;
    float nextAlive = 0.0;

    if (aliveNow > 0.5)  nextAlive = (rule.g > 0.5) ? 1.0 : 0.0;
    else                 nextAlive = (rule.r > 0.5) ? 1.0 : 0.0; 

    FragColor = vec4(nextAlive, self.g, self.b, 1.0);
}
