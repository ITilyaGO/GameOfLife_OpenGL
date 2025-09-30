#version 330 core
out vec4 FragColor;

in vec2 vUV;

uniform sampler2D currentState;
uniform vec2 texelSize;

uniform int birthRules[9];
uniform int survivalRules[9];

float alive(vec2 uv)
{
    return texture(currentState, uv).r > 0.5 ? 1.0 : 0.0;
}

void main()
{
    int count = 0;
    for (int x = -1; x <= 1; x++)
    {
        for (int y = -1; y <= 1; y++)
        {
            if (x == 0 && y == 0) continue;
            vec2 offset = vec2(x, y) * texelSize;
            count += int(alive(vUV + offset));
        }
    }

    float cell = alive(vUV);
    float nextState = 0.0;

    if (cell > 0.5)
    {
        if (survivalRules[count] == 1) nextState = 1.0;
    }
    else
    {
        if (birthRules[count] == 1) nextState = 1.0;
    }

    FragColor = vec4(nextState, nextState, nextState, 1.0);
}
