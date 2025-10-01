#version 330 core
out vec4 FragColor;
in vec2 vUV;

uniform sampler2D currentState;
uniform sampler2D typeColors;

void main()
{
    vec4 cell = texture(currentState, vUV);
    float alive = cell.r;
    int type = int(cell.g * 255.0 + 0.5);

    vec4 color = texelFetch(typeColors, ivec2(type, 0), 0);
    FragColor = mix(vec4(0.0, 0.0, 0.0, 1.0), color, alive);
}
